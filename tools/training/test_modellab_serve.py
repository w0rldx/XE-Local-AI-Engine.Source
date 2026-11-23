"""Self-checks for modellab.py --serve on real CPU torch. Run: ``python3 tools/training/test_modellab_serve.py``.

Assert-based with no framework, like test_modellab_lens.py. transformers is stubbed and no model is loaded: the
end-to-end checks run this file as the worker (``serve-child``), with load_model and trace replaced by fakes on a tiny
torch module, behind real stdin, stdout, threads, signals and exit codes. A job with a routing cap runs the real trace
on a tiny routed module instead, with a CPU stand-in for generate and for what trace reads from CUDA. Where torch is
not installed, every test skips with the reason.
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

from modellablib import CONTRACT_VERSION, ROUTING_WEIGHT_ENCODING, TRACE_KIND

try:
    import torch
except ImportError:
    torch = None

RUN_ID = "run-1"
FRAME_SECONDS = 60
# At least modellab.TOP_CANDIDATES, which a real trace takes from each step's logits.
VOCAB = 24
EXPERTS = 4
TOP_K = 2
ROUTED_TOKENS = 3
PROMPT = [[1, 2, 3]]


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


def _model(routed=False):
    """Two linear blocks under model.layers and a head: what blocks_of, the hooks and the hook check read. Routed, each
    block also runs a router shaped like a transformers top-k MoE gate: (logits, bfloat16 weights, expert ids)."""
    torch = _require_torch()
    nn = torch.nn
    torch.manual_seed(0)

    class Router(nn.Module):
        def __init__(self):
            super().__init__()
            self.proj = nn.Linear(4, EXPERTS)
            self.top_k, self.num_experts, self.norm_topk_prob = TOP_K, EXPERTS, False

        def forward(self, x):
            logits = self.proj(x)
            weights, ids = torch.softmax(logits.float(), -1).topk(TOP_K, -1)
            return logits, weights.to(torch.bfloat16), ids

    class Block(nn.Module):
        def __init__(self):
            super().__init__()
            self.linear = nn.Linear(4, 4)
            self.mlp = nn.Module()
            self.mlp.gate = Router()

        def forward(self, hidden):
            hidden = self.linear(hidden)
            self.mlp.gate(hidden[0])
            return hidden

    class Inner(nn.Module):
        def __init__(self):
            super().__init__()
            self.layers = nn.ModuleList((Block() if routed else nn.Linear(4, 4)) for _ in range(2))

    class Model(nn.Module):
        def __init__(self):
            super().__init__()
            self.model = Inner()
            self.embed = nn.Embedding(VOCAB, 4)
            self.head = nn.Linear(4, VOCAB)
            text_config = types.SimpleNamespace(hidden_size=4)
            self.config = types.SimpleNamespace(
                _attn_implementation="sdpa", model_type="dense", get_text_config=lambda decoder=False: text_config
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


def _tokenizer():
    """What trace reads from a tokenizer: no chat template, no EOS, and a text per id."""
    return types.SimpleNamespace(
        chat_template=None,
        eos_token_id=None,
        unk_token_id=0,
        convert_tokens_to_ids=lambda _token: 0,
        decode=lambda token_ids: "".join(f"<{token}>" for token in token_ids),
    )


def _greedy(model, on_token=None):
    """model.generate as trace calls it, on the CPU: the whole sequence each forward, the processors after it, then
    ``on_token`` and the stopping criterion per token. A routed trace pins the unchunked prefill and nothing else; one
    without routing pins nothing."""
    torch = _require_torch()

    def generate(ids, logits_processor, stopping_criteria, max_new_tokens, **kwargs):
        routed = any(type(processor).__name__ == "_Routing" for processor in logits_processor)
        assert (kwargs.get("prefill_chunk_size", "absent") is None) == routed, kwargs
        assert "past_key_values" not in kwargs, kwargs
        for _ in range(max_new_tokens):
            scores = model(ids).logits[:, -1]
            for processor in logits_processor:
                scores = processor(ids, scores)
            ids = torch.cat([ids, scores.argmax(-1, keepdim=True)], 1)
            if on_token is not None:
                on_token()
            if stopping_criteria[0](ids, scores).all():
                break
        return ids

    return generate


@contextlib.contextmanager
def _on_the_cpu(modellab):
    """The real trace on the CPU: the prompt, the hook records' CUDA statistics and the device facts are stand-ins."""
    torch = _require_torch()

    def tracer(stack, blocks, chosen, _writer, _kinds):
        stack.enter_context(blocks[chosen[0]].register_forward_hook(lambda *_: None))

    with (
        mock.patch.object(modellab, "prompt_ids", return_value=mock.Mock(to=lambda _device: torch.tensor(PROMPT))),
        mock.patch.object(modellab, "_Tracer", tracer),
        mock.patch.object(modellab, "measured_facts", lambda _model, blocks: {"blockCount": len(blocks)}),
        mock.patch.object(torch.cuda, "reset_peak_memory_stats"),
        mock.patch.object(torch.cuda, "synchronize"),
    ):
        yield


@contextlib.contextmanager
def _writers(modellab):
    """The real CappedTraceWriter, keeping every writer the trace opens for the test to inspect."""
    opened = []
    real = modellab.CappedTraceWriter

    def writer(*args, **kwargs):
        opened.append(real(*args, **kwargs))
        return opened[-1]

    with mock.patch.object(modellab, "CappedTraceWriter", writer):
        yield opened


def _routed_job(directory):
    return {
        "modelKey": "k",
        "layers": [0],
        "prompt": "p",
        "maxNewTokens": ROUTED_TOKENS,
        "seed": 1,
        "tracePath": os.path.join(directory, "trace.jsonl"),
        "resultPath": os.path.join(directory, "result.json"),
        "traceByteCap": 4096,
        "routingByteCap": 4096,
    }


def _routed_slot(modellab, on_token=None):
    """A routed model loaded in the worker's slot, generating with ``on_token``."""
    model = _model(routed=True)
    model.generate = _greedy(model, on_token)
    modellab._SLOT.loaded, modellab._SLOT.key = (_tokenizer(), model), "k"
    return model


def _routing_lines(job):
    """The routing records and their footer from a trace file, in file order."""
    with open(job["tracePath"], encoding="utf-8") as handle:
        lines = [json.loads(line) for line in handle]
    return [line for line in lines if "routing" in line], [line for line in lines if "routingFooter" in line]


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
    attention_blocks = [types.SimpleNamespace(self_attn=type("Attention", (), {"head_dim": 2})()) for _ in range(2)]
    try:
        with (
            mock.patch.object(modellab, "_attention_functions", return_value=registry),
            contextlib.ExitStack() as stack,
        ):
            modellab._Candidates(stack, model)
            modellab._Lens(stack, model, model.model.layers, 2, None, model.head)
            modellab._AttentionRows(stack, attention_blocks, [1], 3, 4)
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

    def tracer(stack, blocks, chosen, _writer, _kinds):
        stack.enter_context(blocks[chosen[0]].register_forward_hook(lambda *_: None))

    model.generate = generate
    modellab._SLOT.loaded, modellab._SLOT.key = (types.SimpleNamespace(eos_token_id=0, unk_token_id=0), model), "k"
    modellab._SLOT.loaded[0].convert_tokens_to_ids = lambda _token: 0
    ids = torch.tensor([[1, 2, 3]])
    job = {"modelKey": "k", "layers": [0], "prompt": "p", "maxNewTokens": 4, "seed": 1}
    events = []
    with (
        tempfile.TemporaryDirectory() as directory,
        mock.patch.object(modellab, "prompt_ids", return_value=mock.Mock(to=lambda _device: ids)),
        mock.patch.object(modellab, "_Tracer", tracer),
        _writers(modellab) as writers,
        mock.patch.object(modellab, "check_aligned") as check_aligned,
        mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))),
        mock.patch.object(torch.cuda, "reset_peak_memory_stats"),
        mock.patch.object(torch.cuda, "synchronize"),
    ):
        paths = {"tracePath": os.path.join(directory, "trace.jsonl"), "resultPath": os.path.join(directory, "r.json")}
        try:
            modellab.trace({**job, **paths, "traceByteCap": 1000}, run)
        except modellab.JobCancelled:
            pass
        else:
            raise AssertionError("A trace cancelled during generate must end as cancelled.")
    assert seen == {"stop": [True], "hooks": 2}
    assert _hooks(model) == 0
    assert [writer._handle.closed for writer in writers] == [True], "A cancelled trace closes its file."
    check_aligned.assert_not_called()
    assert [fields.get("phase") for event, fields in events if event == "phase"] == ["tracing"]
    assert all(fields["jobId"] == "j" for _, fields in events)


def test_a_routed_trace_writes_one_routing_line_per_token_in_its_own_section_and_its_summary():
    modellab = _load_modellab()
    model = _routed_slot(modellab)
    with tempfile.TemporaryDirectory() as directory, _on_the_cpu(modellab), mock.patch.object(modellab, "emit"):
        job = _routed_job(directory)
        modellab.trace(job, modellab._Run("j"))
        result = _result(job)
        records, footers = _routing_lines(job)
        with open(job["tracePath"], encoding="utf-8") as handle:
            last = json.loads(handle.readlines()[-1])
    assert _hooks(model) == 0
    assert result["routing"] == {
        "layers": [0, 1],
        "experts": EXPERTS,
        "expertsPerToken": TOP_K,
        "normalizedTopK": False,
        "weightEncoding": ROUTING_WEIGHT_ENCODING,
        "records": ROUTED_TOKENS,
        "truncated": False,
    }
    assert len(result["tokens"]) == ROUTED_TOKENS
    assert [record["routing"]["s"] for record in records] == list(range(ROUTED_TOKENS))
    for record in records:
        ids, bits = record["routing"]["x"], record["routing"]["w"]
        assert [len(row) for row in ids] == [len(row) for row in bits] == [TOP_K, TOP_K], record
        assert all(0 <= expert < EXPERTS for row in ids for expert in row), record
        assert all(0 < weight <= 0x3F80 for row in bits for weight in row), record
    assert [footer["routingFooter"]["records"] for footer in footers] == [ROUTED_TOKENS]
    assert last["footer"] is True, "The hook records' footer closes the file, after the routing section."


def test_a_dense_model_given_a_routing_cap_records_no_routing_and_pins_nothing():
    modellab = _load_modellab()
    model = _model()
    model.generate = _greedy(model)
    modellab._SLOT.loaded, modellab._SLOT.key = (_tokenizer(), model), "k"
    with tempfile.TemporaryDirectory() as directory, _on_the_cpu(modellab), mock.patch.object(modellab, "emit"):
        job = _routed_job(directory)
        modellab.trace(job, modellab._Run("j"))
        assert "routing" not in _result(job)
        assert _routing_lines(job) == ([], [])
    assert _hooks(model) == 0


def test_a_routed_trace_whose_router_registration_fails_midway_leaves_no_hook():
    torch = _require_torch()
    modellab = _load_modellab()
    model = _routed_slot(modellab)
    with (
        tempfile.TemporaryDirectory() as directory,
        _on_the_cpu(modellab),
        mock.patch.object(modellab, "emit"),
        _writers(modellab) as writers,
        # The hook records' (1), the candidates' (2), block 0's router (3), then block 1's router fails.
        mock.patch.object(torch.nn.Module, "register_forward_hook", _register_failing_on(4)),
    ):
        try:
            modellab.trace(_routed_job(directory), modellab._Run("j"))
        except RuntimeError as error:
            assert "first hook" in str(error), str(error)
        else:
            raise AssertionError("The injected registration failure must surface.")
    assert _hooks(model) == 0
    assert [writer._handle.closed for writer in writers] == [True], "A failed trace closes its file."


def test_a_cancel_during_a_routed_generation_ends_it_cancelled_before_the_routing_check_with_no_hook_left():
    modellab = _load_modellab()
    run = modellab._Run("j")
    model = _routed_slot(modellab, on_token=run.cancel.set)
    with (
        tempfile.TemporaryDirectory() as directory,
        _on_the_cpu(modellab),
        mock.patch.object(modellab, "emit"),
        _writers(modellab) as writers,
    ):
        job = _routed_job(directory)
        try:
            modellab.trace(job, run)
        except modellab.JobCancelled:
            pass
        else:
            raise AssertionError("A trace cancelled during generate must end as cancelled, not fail its row count.")
        assert not os.path.exists(job["resultPath"])
    assert _hooks(model) == 0
    assert [writer._handle.closed for writer in writers] == [True], "A cancelled trace closes its file."


def test_a_cancel_while_a_routed_trace_saves_lets_it_finish_whole():
    modellab = _load_modellab()
    run = modellab._Run("j")
    model = _routed_slot(modellab)
    recorded = modellab._Routing.recorded

    def cancelled_while_saving(routing, generated):
        run.cancel.set()
        return recorded(routing, generated)

    with (
        tempfile.TemporaryDirectory() as directory,
        _on_the_cpu(modellab),
        mock.patch.object(modellab, "emit"),
        mock.patch.object(modellab._Routing, "recorded", cancelled_while_saving),
    ):
        job = _routed_job(directory)
        modellab.trace(job, run)
        assert run.cancel.is_set()
        assert _result(job)["routing"]["records"] == ROUTED_TOKENS
        assert len(_routing_lines(job)[0]) == ROUTED_TOKENS
    assert _hooks(model) == 0


def test_derive_on_a_config_that_collects_router_logits_turns_them_off_and_leaves_no_hook():
    torch = _require_torch()
    for flag in (True, None):
        modellab = _load_modellab()
        model = _model()
        if flag is not None:
            model.config.get_text_config().output_router_logits = flag
        forward, seen = model.forward, []

        def capturing(ids, _forward=forward, _model=model, _seen=seen, **kwargs):
            # As transformers' capture_outputs: the kwarg, else the config, installs hooks that outlive the call.
            _seen.append(kwargs)
            if kwargs.get("output_router_logits", getattr(_model.config.get_text_config(), "output_router_logits", 0)):
                _model.model.layers[0].register_forward_hook(lambda *_: None)
            return _forward(ids, **kwargs)

        model.forward = capturing

        def tokenizer(text, **_):
            # Prompts of different lengths, so the two sets' means differ.
            return types.SimpleNamespace(input_ids=mock.Mock(to=lambda _device: torch.tensor([[len(text), 1, 2]])))

        modellab._SLOT.loaded, modellab._SLOT.key = (tokenizer, model), "k"
        with tempfile.TemporaryDirectory() as directory, _on_the_cpu(modellab), mock.patch.object(modellab, "emit"):
            job = {
                "modelKey": "k",
                "layer": 1,
                "seed": 1,
                "positivePrompts": ["a"],
                "negativePrompts": ["bbb"],
                **{name: os.path.join(directory, name) for name in ("vectorPath", "randomVectorPath", "resultPath")},
            }
            modellab.derive(job, modellab._Run("j"))
            assert _result(job)["steering"]["layer"] == 1
        expected = {"output_router_logits": False} if flag else {}
        assert seen == [{"use_cache": False, **expected}] * 2, (flag, seen)
        with mock.patch.object(modellab, "_attention_functions", return_value={"sdpa": _kernel}):
            assert modellab._left_behind(model, _kernel) is None, flag


# --- End to end: this file as the worker, driven over its real stdin and stdout. ---


def _serve_child():
    """The worker with fakes for load_model and trace; each job's prompt says how its trace behaves. A job with a
    routing cap runs the real trace on a routed model, waiting after its first token for a cancel when its prompt says
    so."""
    torch = _require_torch()
    modellab = _load_modellab()
    registry: dict[str, object] = {"sdpa": _kernel}
    modellab._attention_functions = lambda: registry
    real_trace = modellab.trace
    routed = types.SimpleNamespace(run=None, wait=False)

    def wait_when_asked():
        if routed.wait:
            routed.run.cancel.wait(FRAME_SECONDS)

    def load_model(job, run):
        run.enter("loading")
        if job.get("routingByteCap") is None:
            return job["checkpointPath"], _model()
        model = _model(routed=True)
        model.generate = _greedy(model, wait_when_asked)
        return _tokenizer(), model

    def trace(job, run):
        if job.get("routingByteCap") is not None:
            routed.run, routed.wait = run, job["prompt"] == "wait-for-cancel"
            return real_trace(job, run)
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
    with _on_the_cpu(modellab):
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

    def run(self, job_id, prompt, model_key="model-a", kind=TRACE_KIND, routed=False):
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
        if routed:
            job.update(maxNewTokens=ROUTED_TOKENS, routingByteCap=4096)
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
            assert handshake["contractVersion"] == CONTRACT_VERSION == 8
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


def test_a_warm_second_routed_run_records_fresh_rows_of_its_own():
    with _worker() as worker:
        first = worker.run("job-1", "ok", routed=True)
        assert worker.frames_until("ready")[-2]["cancelled"] is False
        second = worker.run("job-2", "ok", routed=True)
        assert worker.frames_until("ready")[-2]["cancelled"] is False
        assert (_result(first)["warm"], _result(second)["warm"]) == (False, True)
        for job in (first, second):
            assert _result(job)["routing"]["records"] == ROUTED_TOKENS
            records, footers = _routing_lines(job)
            assert [record["routing"]["s"] for record in records] == list(range(ROUTED_TOKENS)), "Only this run's rows."
            assert [footer["routingFooter"]["records"] for footer in footers] == [ROUTED_TOKENS]
        assert _routing_lines(first) == _routing_lines(second), "The same model and prompt route the same way."
        worker.send({"op": "evict"})
        assert worker.exit_code() == 0


def test_evict_during_a_routed_generation_ends_it_cancelled_deletes_its_outputs_and_exits_0():
    with _worker() as worker:
        job = worker.run("job-1", "wait-for-cancel", routed=True)
        worker.frames_until("phase")  # loading
        worker.frames_until("phase")  # tracing: the routers are hooked
        worker.send({"op": "evict"})
        frames = worker.frames_until("ready")
        assert _job_events(frames, "job-1") == [("done", None), ("ready", None)], frames
        assert frames[-2]["cancelled"] is True
        assert not os.path.exists(job["tracePath"])
        assert not os.path.exists(job["resultPath"])
        assert worker.exit_code() == 0, "A router hook left behind would have poisoned the worker: exit 1, no ready."


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
