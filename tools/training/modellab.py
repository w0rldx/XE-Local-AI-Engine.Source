"""Model Lab: traces chosen layers of a local safetensors checkpoint with read-only forward hooks.

Input is one ``job.json`` (see ``ModelLabTraceJobV1`` on the C# side). The host fixes the layers, token count and byte
cap; nothing in the job can name code to run. Offline, no remote code, no unsloth.

The protocol is the trainer's JSON-lines shape with its own contract version, on stdout:

    {"event": "handshake", "contractVersion": 1, ...versions}
    {"event": "phase",     "phase": "loading" | "tracing" | "saving"}
    {"event": "progress",  "step", "totalSteps", "vramBytes"}
    {"event": "heartbeat", "phase"}          until the first progress, and while saving; tokens prove life between
    {"event": "done",      "cancelled": bool}
    {"event": "error",     "category", "message"}

Exit status: 0 on success, 3 on a cooperative SIGTERM stop, 1 on any error.
"""

import argparse
import json
import os
import platform
import signal
import sys
import threading
import time

import torch
import transformers
from transformers import AutoModelForCausalLM, AutoTokenizer, StoppingCriteria, StoppingCriteriaList

from modellablib import (
    CONTRACT_VERSION,
    CappedTraceWriter,
    JobError,
    describe_failure,
    heartbeat_counts,
    layer_kind,
    out_of_range_layers,
    trace_record,
    validate_job,
)
from trainlib import CANCELLED_EXIT_CODE

HEARTBEAT_SECONDS = 20
TOP_DIMENSIONS = 8
PERCENTILES = (0.01, 0.5, 0.99)


def emit(event, **fields):
    # os.write, not print: the SIGTERM handler and the heartbeat thread both write here, and one write is one line.
    os.write(1, (json.dumps({"event": event, **fields}) + "\n").encode())


class _Run:
    """What the signal handler, the heartbeat and the stopping criterion share."""

    def __init__(self):
        self.phase = "loading"
        self.progress_seen = False
        self.stop = threading.Event()
        self._finished = threading.Event()

    def enter(self, phase):
        self.phase = phase
        emit("phase", phase=phase)

    def on_sigterm(self, _signum, _frame):
        self.stop.set()
        # Generation stops itself at the next token. Any other phase has no such point, and nothing worth keeping.
        if self.phase != "tracing":
            emit("done", cancelled=True)
            os._exit(CANCELLED_EXIT_CODE)

    def start_heartbeat(self):
        threading.Thread(target=self._beat, daemon=True).start()

    def finish(self):
        self._finished.set()

    def _beat(self):
        while not self._finished.wait(HEARTBEAT_SECONDS):
            if heartbeat_counts(self.phase, self.progress_seen):
                emit("heartbeat", phase=self.phase)


class _Tracer:
    """Per-token statistics from forward hooks on the chosen layers. They return nothing, so they change no value."""

    def __init__(self, blocks, chosen, writer):
        self._writer = writer
        self._first = chosen[0]
        self._step = -1
        self._quantiles = torch.tensor(PERCENTILES, device="cuda")
        self._handles = [
            blocks[i].register_forward_hook(self._hook(i, layer_kind(getattr(blocks[i], "block_type", None))))
            for i in chosen
        ]

    def _hook(self, index, kind):
        def run(_module, _args, output):
            if index == self._first:
                self._step += 1
            if self._writer.truncated:
                self._writer.dropped += 1
                return
            hidden = output[0] if isinstance(output, tuple) else output
            # Only the newest position: a prompt pass would otherwise cost one record per prompt token.
            values = hidden[0, -1].float()
            top = values.abs().topk(TOP_DIMENSIONS)
            self._writer.write(
                trace_record(
                    self._step,
                    index,
                    kind,
                    values.norm().item(),
                    torch.quantile(values, self._quantiles).tolist(),
                    top.indices.tolist(),
                    values[top.indices].tolist(),
                )
            )

        return run

    def remove(self):
        for handle in self._handles:
            handle.remove()


class _ProgressAndStop(StoppingCriteria):
    def __init__(self, run, prompt_tokens, total):
        self._run = run
        self._prompt_tokens = prompt_tokens
        self._total = total

    def __call__(self, input_ids, scores, **kwargs):
        self._run.progress_seen = True
        emit(
            "progress",
            step=input_ids.shape[1] - self._prompt_tokens,
            totalSteps=self._total,
            vramBytes=torch.cuda.memory_allocated(),
        )
        return torch.full((input_ids.shape[0],), self._run.stop.is_set(), device=input_ids.device, dtype=torch.bool)


def blocks_of(model):
    inner = model.model
    return inner.language_model.layers if hasattr(inner, "language_model") else inner.layers


def prompt_ids(tokenizer, prompt):
    if not tokenizer.chat_template:
        return tokenizer(prompt, return_tensors="pt").input_ids
    rendered = tokenizer.apply_chat_template(
        [{"role": "user", "content": prompt}], tokenize=False, add_generation_prompt=True, enable_thinking=False
    )
    return tokenizer(rendered, return_tensors="pt", add_special_tokens=False).input_ids


def stop_tokens(tokenizer, generation_eos):
    # The explicit list replaces the checkpoint's own, so its stops are carried over. A checkpoint with no
    # generation_config stops only on its EOS, and a chat model would run past its end of turn.
    tokens = {tokenizer.eos_token_id}
    tokens.update(generation_eos if isinstance(generation_eos, (list, tuple)) else [generation_eos])
    end_of_turn = tokenizer.convert_tokens_to_ids("<|im_end|>")
    if isinstance(end_of_turn, int) and end_of_turn != tokenizer.unk_token_id:
        tokens.add(end_of_turn)
    return sorted(token for token in tokens if token is not None)


def trace(job, run):
    run.enter("loading")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to the training runtime, and Model Lab traces on the GPU only.")
    path = job["checkpointPath"]
    # A local directory fixed by the host, offline, so there is no revision to pin.
    tokenizer = AutoTokenizer.from_pretrained(path, local_files_only=True, trust_remote_code=False)  # nosec B615
    model = AutoModelForCausalLM.from_pretrained(  # nosec B615
        path, dtype=torch.bfloat16, local_files_only=True, trust_remote_code=False, use_safetensors=True
    )
    model = model.to("cuda").eval()
    blocks = blocks_of(model)
    if outside := out_of_range_layers(job["layers"], len(blocks)):
        raise JobError(f"The job names layers {outside}, and this checkpoint has {len(blocks)} blocks.")

    ids = prompt_ids(tokenizer, job["prompt"]).to("cuda")
    prompt_tokens = ids.shape[1]
    total = job["maxNewTokens"]
    torch.manual_seed(job["seed"])
    torch.cuda.manual_seed_all(job["seed"])
    torch.cuda.reset_peak_memory_stats()

    writer = CappedTraceWriter(job["tracePath"], job["traceByteCap"])
    tracer = _Tracer(blocks, job["layers"], writer)
    run.enter("tracing")
    started = time.perf_counter()
    try:
        with torch.inference_mode():
            output = model.generate(
                ids,
                attention_mask=torch.ones_like(ids),
                do_sample=False,
                max_new_tokens=total,
                use_cache=True,
                eos_token_id=stop_tokens(tokenizer, model.generation_config.eos_token_id),
                pad_token_id=tokenizer.eos_token_id,
                stopping_criteria=StoppingCriteriaList([_ProgressAndStop(run, prompt_tokens, total)]),
            )
        torch.cuda.synchronize()
        seconds = time.perf_counter() - started
    finally:
        tracer.remove()
    if run.stop.is_set():
        return True

    run.enter("saving")
    writer.close()
    generated = output[0, prompt_tokens:].tolist()
    result = {
        "promptTokens": prompt_tokens,
        "tokens": [{"id": token, "text": tokenizer.decode([token])} for token in generated],
        "text": tokenizer.decode(generated),
        "seconds": round(seconds, 3),
        "peakVramBytes": torch.cuda.max_memory_allocated(),
        "blockCount": len(blocks),
        "modelClass": type(model).__name__,
        "device": torch.cuda.get_device_name(0),
        "torch": torch.__version__,
        "transformers": transformers.__version__,
        "python": platform.python_version(),
    }
    with open(job["resultPath"], "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False)
    return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    arguments = parser.parse_args()

    # What it writes holds the prompt's output and statistics: owner-only even if the work directory's mode failed.
    os.umask(0o077)
    run = _Run()
    signal.signal(signal.SIGTERM, run.on_sigterm)
    emit("handshake", contractVersion=CONTRACT_VERSION, torch=torch.__version__, transformers=transformers.__version__)
    run.start_heartbeat()
    try:
        with open(arguments.config, encoding="utf-8") as handle:
            cancelled = trace(validate_job(json.load(handle)), run)
    except JobError as error:
        emit("error", category="contract", message=str(error))
        return 1
    except torch.cuda.OutOfMemoryError:
        emit("error", category="out-of-memory", message="The GPU ran out of memory during the trace.")
        return 1
    except Exception as error:
        emit("error", category="trace", message=describe_failure(error))
        return 1
    finally:
        run.finish()

    emit("done", cancelled=cancelled)
    return CANCELLED_EXIT_CODE if cancelled else 0


if __name__ == "__main__":
    sys.exit(main())
