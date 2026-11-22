"""Self-checks for modellab.py --serve on real CPU torch. Run: ``python3 tools/training/test_modellab_serve.py``.

Assert-based with no framework, like test_modellab_lens.py. transformers is stubbed and no model is loaded: the
end-to-end checks run this file as the worker (``serve-child``), with load_model and trace replaced by fakes on a tiny
torch module, behind real stdin, stdout, threads, signals and exit codes. Where torch is not installed, every test
skips with the reason.
"""

import contextlib
import json
import os
import queue
import signal
import subprocess
import sys
import tempfile
import threading
import types
import unittest
from unittest import mock

from modellablib import CONTRACT_VERSION, TRACE_KIND

try:
    import torch
except ImportError:
    torch = None

RUN_ID = "run-1"
FRAME_SECONDS = 60


def _require_torch():
    if torch is None:
        raise unittest.SkipTest("torch is not installed: the root dev venv carries CPU torch on linux x86_64 only.")
    return torch


def _load_modellab():
    """The real script on real torch, with transformers reduced to the names it imports. Leaves no stub behind."""
    _require_torch()
    transformers = types.SimpleNamespace(
        __version__="stub",
        AutoModelForCausalLM=None,
        AutoTokenizer=None,
        DynamicCache=None,
        LogitsProcessor=object,
        LogitsProcessorList=list,
        StoppingCriteria=object,
        StoppingCriteriaList=list,
    )
    with mock.patch.dict(sys.modules, {"transformers": transformers}):
        sys.modules.pop("modellab", None)
        import modellab

        sys.modules.pop("modellab", None)
    return modellab


def _kernel(*_args, **_kwargs):
    raise AssertionError("The stub sdpa kernel never runs.")


def _model():
    """Two linear blocks under model.layers and a head: what blocks_of, the hooks and the hook check read."""
    torch = _require_torch()
    nn = torch.nn

    class Inner(nn.Module):
        def __init__(self):
            super().__init__()
            self.layers = nn.ModuleList(nn.Linear(4, 4) for _ in range(2))

    class Model(nn.Module):
        def __init__(self):
            super().__init__()
            self.model = Inner()
            self.embed = nn.Embedding(8, 4)
            self.head = nn.Linear(4, 8)
            text_config = types.SimpleNamespace(hidden_size=4)
            self.config = types.SimpleNamespace(
                _attn_implementation="sdpa", get_text_config=lambda decoder=False: text_config
            )
            self.generation_config = types.SimpleNamespace(eos_token_id=None)

        def get_input_embeddings(self):
            return self.embed

        def forward(self, ids, **_kwargs):
            hidden = self.embed(ids)
            for block in self.model.layers:
                hidden = block(hidden)
            return types.SimpleNamespace(logits=self.head(hidden))

    return Model().eval()


def _hooks(model):
    return sum(len(m._forward_hooks) + len(m._forward_pre_hooks) for m in model.modules())


def _register_failing_on(call):
    """register_forward_hook that registers for real, then raises on its ``call``-th use, as a constructor would."""
    original = _require_torch().nn.Module.register_forward_hook
    count = [0]

    def register(module, *args, **kwargs):
        count[0] += 1
        if count[0] == call:
            raise RuntimeError("injected after the first hook")
        return original(module, *args, **kwargs)

    return register


class _SwapThenFail(dict):
    """An attention registry whose swap takes effect and then raises, like a constructor failing right after it."""

    def __setitem__(self, key, value):
        super().__setitem__(key, value)
        if value is not _kernel:
            raise RuntimeError("injected after the sdpa swap")


def test_progress_stops_generation_once_the_job_is_cancelled():
    torch = _require_torch()
    modellab = _load_modellab()
    run = modellab._Run("j")
    progress = modellab._Progress(run, 3, 8)
    ids = torch.zeros((1, 5), dtype=torch.long)
    with mock.patch.object(modellab, "emit"):
        assert progress(ids, None).tolist() == [False]
        run.cancel.set()
        assert progress(ids, None).tolist() == [True]


def test_a_constructor_failing_after_its_first_hook_leaves_no_hook():
    torch = _require_torch()
    modellab = _load_modellab()
    model = _model()
    blocks = model.model.layers
    try:
        with (
            contextlib.ExitStack() as stack,
            mock.patch.object(torch.nn.Module, "register_forward_hook", _register_failing_on(3)),
        ):
            modellab._Candidates(stack, model)
            # The lens registers block 0's hook (the second), then fails on the model's (the third).
            modellab._Lens(stack, model, blocks, 2, None, model.head)
    except RuntimeError as error:
        assert "first hook" in str(error), str(error)
    else:
        raise AssertionError("The injected registration failure must surface.")
    assert _hooks(model) == 0


def test_a_constructor_failing_right_after_the_sdpa_swap_restores_the_kernel_and_every_hook():
    modellab = _load_modellab()
    model = _model()
    registry = _SwapThenFail(sdpa=_kernel)
    qwen = types.SimpleNamespace(Qwen3_5Attention=type("Qwen3_5Attention", (), {}))
    try:
        with (
            mock.patch.dict(sys.modules, {"transformers.models.qwen3_5.modeling_qwen3_5": qwen}),
            mock.patch.object(modellab, "_attention_functions", return_value=registry),
            contextlib.ExitStack() as stack,
        ):
            modellab._Candidates(stack, model)
            modellab._Lens(stack, model, model.model.layers, 2, None, model.head)
            modellab._AttentionRows(stack, [1], 3, 4)
    except RuntimeError as error:
        assert "sdpa swap" in str(error), str(error)
    else:
        raise AssertionError("The injected failure after the swap must surface.")
    assert _hooks(model) == 0
    assert registry["sdpa"] is _kernel


def test_a_job_that_leaves_a_hook_or_the_swap_behind_is_caught():
    modellab = _load_modellab()
    model = _model()
    registry: dict[str, object] = {"sdpa": _kernel}
    with mock.patch.object(modellab, "_attention_functions", return_value=registry):
        assert modellab._left_behind(model, _kernel) is None
        assert modellab._left_behind(None, _kernel) is None
        handle = model.model.layers[1].register_forward_hook(lambda *_: None)
        assert "model.layers.1" in (modellab._left_behind(model, _kernel) or "")
        handle.remove()
        registry["sdpa"] = _register_failing_on
        assert "sdpa" in (modellab._left_behind(model, _kernel) or "")


def test_the_slot_loads_once_and_refuses_another_model_key():
    modellab = _load_modellab()
    model = _model()
    loads = []

    def load(job, run):
        loads.append(job["modelKey"])
        return "tokenizer", model

    run = modellab._Run("j")
    with mock.patch.object(modellab, "load_model", load):
        tokenizer, loaded, warm, _ = modellab._SLOT.take({"modelKey": "a"}, run)
        assert (tokenizer, loaded, warm) == ("tokenizer", model, False)
        assert modellab._SLOT.take({"modelKey": "a"}, run)[2:] == (True, 0.0)
        try:
            modellab._SLOT.take({"modelKey": "b"}, run)
        except modellab.JobError as error:
            assert "another model" in str(error), str(error)
        else:
            raise AssertionError("A job naming another model must be refused, not served the loaded one.")
    assert loads == ["a"]
    assert modellab._SLOT.model is model


def test_a_cancel_during_generate_stops_it_and_the_trace_ends_before_its_checks_with_no_hook_left():
    torch = _require_torch()
    modellab = _load_modellab()
    model = _model()
    run = modellab._Run("j")
    seen = {}

    def generate(ids, stopping_criteria, **_kwargs):
        model(ids)  # one forward through the job's hooks
        run.cancel.set()
        seen["stop"] = stopping_criteria[0](ids, None).tolist()
        seen["hooks"] = _hooks(model)
        return ids

    def tracer(stack, blocks, chosen, _writer):
        stack.enter_context(blocks[chosen[0]].register_forward_hook(lambda *_: None))

    model.generate = generate
    modellab._SLOT.loaded, modellab._SLOT.key = (types.SimpleNamespace(eos_token_id=0, unk_token_id=0), model), "k"
    modellab._SLOT.loaded[0].convert_tokens_to_ids = lambda _token: 0
    ids = torch.tensor([[1, 2, 3]])
    job = {"modelKey": "k", "layers": [0], "prompt": "p", "maxNewTokens": 4, "seed": 1}
    events = []
    with (
        mock.patch.object(modellab, "prompt_ids", return_value=mock.Mock(to=lambda _device: ids)),
        mock.patch.object(modellab, "_Tracer", tracer),
        mock.patch.object(modellab, "CappedTraceWriter"),
        mock.patch.object(modellab, "check_aligned") as check_aligned,
        mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))),
        mock.patch.object(torch.cuda, "reset_peak_memory_stats"),
        mock.patch.object(torch.cuda, "synchronize"),
    ):
        try:
            modellab.trace({**job, "tracePath": "unused", "traceByteCap": 1000, "resultPath": "unused"}, run)
        except modellab.JobCancelled:
            pass
        else:
            raise AssertionError("A trace cancelled during generate must end as cancelled.")
    assert seen == {"stop": [True], "hooks": 2}
    assert _hooks(model) == 0
    check_aligned.assert_not_called()
    assert [fields.get("phase") for event, fields in events if event == "phase"] == ["tracing"]
    assert all(fields["jobId"] == "j" for _, fields in events)


# --- End to end: this file as the worker, driven over its real stdin and stdout. ---


def _serve_child():
    """The worker with fakes for load_model and trace; each job's prompt says how its trace behaves."""
    torch = _require_torch()
    modellab = _load_modellab()
    registry: dict[str, object] = {"sdpa": _kernel}
    modellab._attention_functions = lambda: registry

    def load_model(job, run):
        run.enter("loading")
        return job["checkpointPath"], _model()

    def trace(job, run):
        _, model, warm, load_seconds = modellab._SLOT.take(job, run)
        behaviour = job["prompt"]
        with contextlib.ExitStack() as stack:
            modellab._Candidates(stack, model)
            run.enter("tracing")
            with open(job["tracePath"], "w", encoding="utf-8") as handle:
                handle.write("partial\n")
            if behaviour == "wait-for-cancel":
                run.cancel.wait(FRAME_SECONDS)
            elif behaviour == "fail":
                raise RuntimeError("boom")
            elif behaviour == "leak-hook":
                model.model.layers[0].register_forward_hook(lambda *_: None)
            elif behaviour == "leak-sdpa":
                registry["sdpa"] = load_model
            modellab._Progress(run, 0, 1)(torch.zeros((1, 1)), None)
        run.enter("saving")
        with open(job["resultPath"], "w", encoding="utf-8") as handle:
            json.dump({"warm": warm, "loadSeconds": load_seconds}, handle)

    modellab.load_model = load_model
    modellab.trace = trace
    sys.argv = ["modellab.py", "--serve"]
    return modellab.main()


class _Worker:
    """A serve-child process: commands in, frames out, each frame awaited with a bound."""

    def __init__(self, directory):
        _require_torch()
        self.directory = directory
        self.process = subprocess.Popen(  # noqa: S603 - this interpreter running this file
            [sys.executable, os.path.abspath(__file__), "serve-child"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env={**os.environ, "XE_TRAINING_RUN_TOKEN": RUN_ID},
        )
        stdin, stdout = self.process.stdin, self.process.stdout
        assert stdin is not None
        assert stdout is not None
        self._stdin, self._stdout = stdin, stdout
        self._frames = queue.Queue()
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._reader.start()

    def _read(self):
        for line in self._stdout:
            self._frames.put(json.loads(line))
        self._frames.put(None)

    def frame(self):
        frame = self._frames.get(timeout=FRAME_SECONDS)
        assert frame is None or frame["token"] == RUN_ID, frame
        return frame

    def handshake(self):
        frame = self.frame()
        assert frame is not None
        assert frame["event"] == "handshake", frame
        return frame

    def frames_until(self, event):
        frames = []
        while (frame := self.frame()) is not None:
            frames.append(frame)
            if frame["event"] == event:
                return frames
        raise AssertionError(f"The worker exited before a {event} frame: {frames}")

    def send(self, command):
        self.send_raw((json.dumps(command) + "\n").encode())

    def send_raw(self, line):
        self._stdin.write(line)
        self._stdin.flush()

    def run(self, job_id, prompt, model_key="model-a", kind=TRACE_KIND):
        directory = os.path.join(self.directory, job_id)
        os.makedirs(directory)
        job = {
            "contractVersion": CONTRACT_VERSION,
            "kind": kind,
            "modelKey": model_key,
            "checkpointPath": "/checkpoint",
            "prompt": prompt,
            "tracePath": os.path.join(directory, "trace.jsonl"),
            "resultPath": os.path.join(directory, "result.json"),
            "layers": [0],
            "maxNewTokens": 1,
            "traceByteCap": 4096,
            "seed": 1,
        }
        if kind != TRACE_KIND:
            job = {key: job[key] for key in ("kind", "contractVersion", "resultPath")}
            job.update(configDirectory=directory, maxResultBytes=1024)
        config = os.path.join(directory, "job.json")
        with open(config, "w", encoding="utf-8") as handle:
            json.dump(job, handle)
        self.send({"op": "run", "jobId": job_id, "config": config})
        return job

    def exit_code(self):
        """Closes stdin (EOF, the same as evict) and waits for the exit status."""
        with contextlib.suppress(BrokenPipeError):
            self._stdin.close()
        return self.process.wait(timeout=FRAME_SECONDS)

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=FRAME_SECONDS)
        self._reader.join(FRAME_SECONDS)
        with contextlib.suppress(BrokenPipeError):
            self._stdin.close()
        self._stdout.close()


@contextlib.contextmanager
def _worker():
    with tempfile.TemporaryDirectory() as directory:
        worker = _Worker(directory)
        try:
            handshake = worker.handshake()
            assert handshake["contractVersion"] == CONTRACT_VERSION == 7
            assert handshake["serve"] is True
            yield worker
        finally:
            worker.close()


def _job_events(frames, job_id):
    assert all(frame.get("jobId") == job_id for frame in frames if frame["event"] != "ready"), frames
    return [(frame["event"], frame.get("phase")) for frame in frames if frame["event"] != "progress"]


def _result(job):
    with open(job["resultPath"], encoding="utf-8") as handle:
        return json.load(handle)


def test_a_served_worker_loads_once_runs_warm_refuses_another_model_and_exits_0_on_eof():
    with _worker() as worker:
        first = worker.run("job-1", "ok")
        frames = worker.frames_until("ready")
        assert _job_events(frames, "job-1") == [
            ("phase", "loading"),
            ("phase", "tracing"),
            ("phase", "saving"),
            ("done", None),
            ("ready", None),
        ]
        assert frames[-2]["cancelled"] is False
        assert "jobId" not in frames[-1]
        assert {"vramBytes", "reservedBytes"} <= frames[-1].keys()
        assert any(frame["event"] == "progress" for frame in frames)
        assert _result(first)["warm"] is False

        second = worker.run("job-2", "ok")
        assert _job_events(worker.frames_until("ready"), "job-2") == [
            ("phase", "tracing"),
            ("phase", "saving"),
            ("done", None),
            ("ready", None),
        ]
        assert _result(second) == {"warm": True, "loadSeconds": 0.0}

        worker.run("job-3", "ok", model_key="model-b")
        error, ready = worker.frames_until("ready")
        assert (error["event"], error["jobId"], error["category"]) == ("error", "job-3", "contract")
        assert ready["event"] == "ready"
        assert worker.exit_code() == 0


def test_a_failing_job_and_a_source_job_end_in_error_then_ready():
    with _worker() as worker:
        worker.run("job-1", "fail")
        frames = worker.frames_until("ready")
        assert [frame["event"] for frame in frames][-2:] == ["error", "ready"]
        assert (frames[-2]["jobId"], frames[-2]["category"], frames[-2]["message"]) == (
            "job-1",
            "trace",
            "RuntimeError: boom",
        )
        worker.run("job-2", "unused", kind="source")
        error, _ = worker.frames_until("ready")
        assert (error["jobId"], error["category"]) == ("job-2", "contract"), error
        worker.send({"op": "evict"})
        assert worker.exit_code() == 0


def test_cancel_ends_the_named_job_as_cancelled_deletes_its_outputs_and_keeps_the_worker():
    with _worker() as worker:
        job = worker.run("job-1", "wait-for-cancel")
        worker.frames_until("phase")  # loading
        worker.frames_until("phase")  # tracing: the partial trace is written
        worker.send({"op": "cancel", "jobId": "another-job"})
        worker.send({"op": "cancel", "jobId": "job-1"})
        frames = worker.frames_until("ready")
        assert _job_events(frames, "job-1") == [("done", None), ("ready", None)], frames
        assert frames[-2]["cancelled"] is True
        assert not os.path.exists(job["tracePath"])

        worker.run("job-2", "ok")
        assert worker.frames_until("ready")[-2]["cancelled"] is False
        worker.send({"op": "evict"})
        assert worker.exit_code() == 0


def test_evict_while_running_cancels_the_job_then_exits_0():
    with _worker() as worker:
        worker.run("job-1", "wait-for-cancel")
        worker.frames_until("phase")
        worker.frames_until("phase")
        worker.send({"op": "evict"})
        frames = worker.frames_until("ready")
        assert frames[-2]["event"] == "done"
        assert frames[-2]["cancelled"] is True
        assert worker.exit_code() == 0


def test_a_command_the_worker_cannot_follow_is_a_protocol_error_and_exit_1():
    for case in ("bad json", "unknown op", "run while busy"):
        with _worker() as worker:
            if case == "bad json":
                worker.send_raw(b"{not json\n")
            elif case == "unknown op":
                worker.send({"op": "shutdown"})
            else:
                worker.run("job-1", "wait-for-cancel")
                worker.frames_until("phase")
                worker.run("job-2", "ok")
            error = worker.frames_until("error")[-1]
            assert error["category"] == "protocol", (case, error)
            assert "jobId" not in error, (case, error)
            assert worker.frame() is None, case
            assert worker.process.wait(timeout=FRAME_SECONDS) == 1, case


def test_a_job_that_leaves_a_hook_or_the_sdpa_swap_behind_poisons_the_worker():
    for behaviour in ("leak-hook", "leak-sdpa"):
        with _worker() as worker:
            worker.run("job-1", behaviour)
            frames = worker.frames_until("error")
            assert [frame["event"] for frame in frames][-2:] == ["done", "error"], frames
            assert frames[-1]["category"] == "worker-poisoned"
            assert "jobId" not in frames[-1]
            assert worker.frame() is None, "No ready after a poisoned cleanup."
            assert worker.process.wait(timeout=FRAME_SECONDS) == 1


def test_sigterm_exits_3_at_once():
    with _worker() as worker:
        worker.process.send_signal(signal.SIGTERM)
        assert worker.process.wait(timeout=FRAME_SECONDS) == 3


if __name__ == "__main__":
    if sys.argv[1:] == ["serve-child"]:
        sys.exit(_serve_child())
    for name, test in sorted(globals().items()):
        if name.startswith("test_") and callable(test):
            try:
                test()
            except unittest.SkipTest as reason:
                print(f"skip {name}: {reason}")
            else:
                print(f"ok {name}")
