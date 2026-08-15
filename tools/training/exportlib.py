"""Pure-python helpers for export.py — no torch, no unsloth, no transformers.

Same split, and the same reason, as trainlib.py: export.py imports unsloth at module scope, which makes the module
unimportable anywhere the training runtime is not installed. Everything here is contract and path work, which is
exactly the part worth a test.

Run the checks with ``python3 tools/training/test_exportlib.py``.
"""

import json
import os

CONTRACT_VERSION = 1

# The only mode export.py itself implements. The adapter export needs no python at all — the host runs
# convert_lora_to_gguf.py against the trainer's own adapter directory — so asking this script for it is a
# configuration mistake rather than a supported path, and saying so beats silently doing nothing.
MERGE_MODE = "merge"

MERGED_DIRECTORY_NAME = "merged-hf"


class ExportConfigError(Exception):
    """A job.json this script cannot act on. Reported as one protocol error line, never as a traceback."""


def validate_config(config):
    """Checks the contract and returns ``(base_path, adapter_dir, merged_dir)``.

    Every path is returned absolute: the subprocess runs with its own working directory, and a relative path in the
    job file would resolve against it rather than against the run, which is the silent version of this going wrong.
    """
    if not isinstance(config, dict):
        raise ExportConfigError("The job configuration is not an object.")

    if config.get("contractVersion") != CONTRACT_VERSION:
        raise ExportConfigError("The job configuration contract version is not supported by this exporter.")

    mode = config.get("mode")
    if mode != MERGE_MODE:
        raise ExportConfigError(f"Unsupported export mode '{mode}'; this script only performs the merge step.")

    base_path = _require_path(config, "basePath")
    adapter_dir = _require_path(config, "adapterDir")
    output_dir = _require_path(config, "outputDir")
    return base_path, adapter_dir, merged_directory(output_dir)


def merged_directory(output_dir):
    """Where the merged 16-bit checkpoint is written. A fixed name under the run's own staged directory."""
    return os.path.join(output_dir, MERGED_DIRECTORY_NAME)


def read_config(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def protocol_line(event, **fields):
    """The exact bytes one protocol line carries. Shared with the host's TrainingRunStdioParser contract."""
    payload = {"event": event}
    payload.update(fields)
    return json.dumps(payload)


def _require_path(config, key):
    value = config.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ExportConfigError(f"The job configuration is missing '{key}'.")
    return os.path.abspath(value)
