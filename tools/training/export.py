"""Merges a trained LoRA adapter back into its base checkpoint, as a 16-bit Hugging Face directory.

Input is a single ``export-job.json`` (see ``TrainingExportJobConfigV1`` on the C# side); output is a merged HF
checkpoint under ``outputDir/merged-hf``. The GGUF conversion and quantization that follow are NOT done here — the
host runs llama.cpp's own ``convert_hf_to_gguf.py`` and ``llama-quantize`` at a pinned commit, so the file a model
is served from is produced by the same source tree as the server that loads it. ``save_pretrained_gguf`` is
deliberately never called for that reason.

The adapter export has no python step at all: the host runs ``convert_lora_to_gguf.py`` straight against the
trainer's adapter directory, so this script only ever performs ``mode: "merge"``.

The protocol is the trainer's, contract version 1 — one JSON object per line, everything else is banner text:

    {"event": "handshake", "contractVersion": 1, ...versions}
    {"event": "phase",     "phase": "loading" | "merging"}
    {"event": "heartbeat", "phase"}
    {"event": "artifact",  "kind": "MergedHfDir", "path"}
    {"event": "done",      "cancelled": false}
    {"event": "error",     "category", "message"}

Exit status: 0 on success, 1 on any error.
"""

# Import order matters: unsloth patches transformers/TRL at import time and warns loudly if it is imported after
# them. This is an unsloth requirement, not a style preference.
import unsloth  # noqa: F401  isort:skip

import argparse
import sys
import threading
import traceback

from exportlib import CONTRACT_VERSION, ExportConfigError, protocol_line, read_config, validate_config

HEARTBEAT_SECONDS = 20


def emit(event, **fields):
    """Writes one protocol line. Flushed every time: the host's watchdog reads liveness from these arriving."""
    print(protocol_line(event, **fields), flush=True)


def fail(category, message):
    emit("error", category=category, message=message)
    sys.exit(1)


class _Heartbeat:
    """Emits a heartbeat while a long phase produces no other output.

    Merging writes several gigabytes of fp16 weights with no progress output of its own, which the host cannot tell
    apart from a wedged process. The beat is what makes the inactivity watchdog safe to keep short.
    """

    def __init__(self):
        self._phase = "loading"
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self):
        self._thread.start()

    def phase(self, name):
        self._phase = name
        emit("phase", phase=name)

    def stop(self):
        self._stop.set()

    def _run(self):
        while not self._stop.wait(HEARTBEAT_SECONDS):
            emit("heartbeat", phase=self._phase)


def main():
    parser = argparse.ArgumentParser(description="Merges a trained LoRA adapter into its base checkpoint.")
    parser.add_argument("--config", required=True, help="Path to the run's export-job.json.")
    arguments = parser.parse_args()

    try:
        base_path, adapter_dir, merged_dir = validate_config(read_config(arguments.config))
    except ExportConfigError as error:
        fail("contract", str(error))
    except (OSError, ValueError) as error:
        fail("contract", f"The job configuration could not be read: {error}")

    import torch
    from unsloth import FastLanguageModel

    emit(
        "handshake",
        contractVersion=CONTRACT_VERSION,
        torch=torch.__version__,
        cuda=torch.version.cuda,
        device=torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
        # Echoed so a merge that resolved a DIFFERENT base than the run trained against is visible in the log tail:
        # peft resolves the base from the adapter's own config, and this is what the host expected it to find.
        base=base_path,
    )

    heartbeat = _Heartbeat()
    heartbeat.start()
    try:
        heartbeat.phase("loading")
        # Loaded from the ADAPTER directory: peft records its base model there, and unsloth reads the adapter
        # config to rebuild the pair. Loading the base and attaching the adapter separately would work too, but it
        # duplicates the resolution peft already did and can disagree with it.
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_name=adapter_dir,
            # 16-bit merge: loading the base in 4 bits and merging into it would bake the quantization error into
            # the merged weights, and the GGUF quantizer downstream would then quantize an already-lossy model.
            load_in_4bit=False,
            local_files_only=True,
        )

        heartbeat.phase("merging")
        # The one supported merge call. save_pretrained_gguf is excluded on purpose (see the module docstring).
        model.save_pretrained_merged(merged_dir, tokenizer, save_method="merged_16bit")
        emit("artifact", kind="MergedHfDir", path=merged_dir)
        emit("done", cancelled=False)
    except SystemExit:
        raise
    except Exception as error:  # noqa: BLE001 - every failure has to reach the host as one protocol line
        emit("error", category=type(error).__name__, message=str(error)[:1000])
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
    finally:
        heartbeat.stop()

    sys.exit(0)


if __name__ == "__main__":
    main()
