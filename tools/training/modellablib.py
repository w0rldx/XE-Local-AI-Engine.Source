"""Pure-python helpers for modellab.py: no torch, no transformers and no gguf, so they run without a GPU or the venv.

Run the checks with ``python3 tools/training/test_modellablib.py``.
"""

import json
import math
import struct
from typing import Any, TypeGuard

# Separate from trainlib.CONTRACT_VERSION: this script and train.py change independently.
CONTRACT_VERSION = 2

TRACE_KIND = "trace"
STEERING_KIND = "steering"

# Room kept for the footer line, so the file including its footer can never exceed the cap.
FOOTER_RESERVE_BYTES = 160

MAX_NEW_TOKENS_LIMIT = 4096

# A vector this long against the residual norm: the spike's choice from six scales.
STEERING_NORM_FACTOR = 0.5

# What llama.cpp's loader reads: a GGUF v3 file with one 1-D F32 tensor named direction.<layer>, data aligned to 32.
_GGUF_STRING_TYPE = 8
_GGUF_F32_TYPE = 0
_GGUF_ALIGNMENT = 32


class JobError(ValueError):
    """A job file the script cannot use; modellab.py reports it as the "contract" error category."""


def validate_job(job):
    """Checks every field the host fixes, before the model is loaded."""
    if not isinstance(job, dict):
        raise JobError("The job file is not a JSON object.")
    if job.get("contractVersion") != CONTRACT_VERSION:
        raise JobError("The job's contract version is not supported by this script.")
    kind = job.get("kind")
    if kind == STEERING_KIND:
        return _validated_steering_job(job)
    if kind != TRACE_KIND:
        raise JobError("The job has no usable 'kind'.")
    _require_strings(job, ("checkpointPath", "prompt", "tracePath", "resultPath"))
    layers = job.get("layers")
    if not isinstance(layers, list) or not layers or not all(_is_int(layer) and layer >= 0 for layer in layers):
        raise JobError("The job has no usable 'layers'.")
    if len(set(layers)) != len(layers):
        raise JobError("The job names a layer twice.")
    # A step is counted when the first listed layer's hook fires: an earlier block listed later lands a step behind.
    if layers != sorted(layers):
        raise JobError("The job's 'layers' are not in ascending order.")
    tokens = job.get("maxNewTokens")
    if not _is_int(tokens) or not 1 <= tokens <= MAX_NEW_TOKENS_LIMIT:
        raise JobError("The job has no usable 'maxNewTokens'.")
    cap = job.get("traceByteCap")
    if not _is_int(cap) or cap <= FOOTER_RESERVE_BYTES:
        raise JobError("The job has no usable 'traceByteCap'.")
    if not _is_int(job.get("seed")):
        raise JobError("The job has no usable 'seed'.")
    return job


def _validated_steering_job(job):
    _require_strings(job, ("checkpointPath", "vectorPath", "randomVectorPath", "resultPath"))
    for name in ("positivePrompts", "negativePrompts"):
        prompts = job.get(name)
        usable = (
            isinstance(prompts, list) and prompts and all(isinstance(text, str) and text.strip() for text in prompts)
        )
        if not usable:
            raise JobError(f"The job has no usable '{name}'.")
    layer = job.get("layer")
    # Block 0 has no direction tensor: llama.cpp's loader rejects index 0.
    if not _is_int(layer) or layer < 1:
        raise JobError("The job has no usable 'layer'.")
    if not _is_int(job.get("seed")):
        raise JobError("The job has no usable 'seed'.")
    return job


def _require_strings(job, names):
    for name in names:
        if not isinstance(job.get(name), str) or not job[name]:
            raise JobError(f"The job has no usable '{name}'.")


def _is_int(value: Any) -> TypeGuard[int]:
    return isinstance(value, int) and not isinstance(value, bool)


def out_of_range_layers(layers, block_count):
    return [layer for layer in layers if layer >= block_count]


def layer_kind(block_type):
    """One letter for the trace: L for a linear-attention block, F for a full one, ? when the model does not say."""
    if block_type is None:
        return "?"
    return "L" if block_type == "linear_attention" else "F"


def heartbeat_counts(phase, progress_seen):
    """Whether a heartbeat may speak for this phase.

    Its thread keeps beating while the main thread is wedged, so once tokens or prompts flow only they show life.
    Loading, saving and a first forward that may be compiling kernels have no such events, so there it is all the host
    can get.
    """
    return phase not in ("tracing", "deriving") or not progress_seen


def vector_norm(values):
    return math.sqrt(sum(value * value for value in values))


def scaled_to_norm(values, norm):
    """The same direction at the given length. A direction of no length has none to keep, so it is an error."""
    current = vector_norm(values)
    if not math.isfinite(current) or current == 0:
        raise ValueError("The vector has no usable length: its prompts may not differ at this layer.")
    return [value * norm / current for value in values]


def steering_vector(positive_mean, negative_mean, reference_norm):
    """The sets' mean difference as a direction, at STEERING_NORM_FACTOR times the residual's own length."""
    difference = [positive - negative for positive, negative in zip(positive_mean, negative_mean, strict=True)]
    return scaled_to_norm(difference, STEERING_NORM_FACTOR * reference_norm), vector_norm(difference)


def control_vector_bytes(layer, values):
    """A control vector file. The host's check reads it back against the model before any launch."""
    if layer < 1:
        raise ValueError("Block 0 cannot be steered.")
    if not values or not all(math.isfinite(value) for value in values) or not any(values):
        raise ValueError("A control vector needs finite values that are not all zero.")
    metadata = (
        _gguf_string("general.architecture") + struct.pack("<I", _GGUF_STRING_TYPE) + _gguf_string("controlvector")
    )
    tensor = _gguf_string(f"direction.{layer}") + struct.pack("<IQIQ", 1, len(values), _GGUF_F32_TYPE, 0)
    header = b"GGUF" + struct.pack("<IQQ", 3, 1, 1) + metadata + tensor
    return header + b"\0" * (-len(header) % _GGUF_ALIGNMENT) + struct.pack(f"<{len(values)}f", *values)


def _gguf_string(text):
    encoded = text.encode()
    return struct.pack("<Q", len(encoded)) + encoded


def describe_failure(error):
    """The row's reason. Triton's text for a missing compiler offers remedies a user cannot reach, so it is replaced."""
    if "Failed to find C compiler" in str(error):
        return (
            "The first trace on this machine needs a C compiler, and there is no gcc or clang on the node's PATH. "
            "Install one or make it visible to the node, then start the trace again."
        )
    # Shortened, and the type kept: str(error) can be empty.
    return f"{type(error).__name__}: {str(error)[:400]}"


def _rounded(value):
    # NaN and infinity have no JSON form. Null keeps the file parseable and says the value was not a finite number.
    return round(value, 3) if math.isfinite(value) else None


def trace_record(step, layer, kind, norm, percentiles, top_indices, top_values):
    """One token's statistics at one layer. Rounded so the record stays small; the cap is what bounds the file."""
    return {
        "s": step,
        "l": layer,
        "k": kind,
        "n": _rounded(norm),
        "p": [_rounded(value) for value in percentiles],
        "ti": list(top_indices),
        "tv": [_rounded(value) for value in top_values],
    }


class CappedTraceWriter:
    """JSON lines with a hard byte cap. Once a record does not fit, every later one is counted, not written."""

    def __init__(self, path, cap):
        if cap <= FOOTER_RESERVE_BYTES:
            raise ValueError("The trace byte cap is too small for its footer.")
        self.path = path
        self.cap = cap
        self.bytes = 0
        self.records = 0
        self.dropped = 0
        # The directory is owner-only; the file holds statistics of the prompt's activations.
        self._handle = open(path, "wb")  # noqa: SIM115 - it stays open across the hooks, until close().

    @property
    def truncated(self):
        return self.dropped > 0

    def write(self, record):
        if self.truncated:
            self.dropped += 1
            return False
        line = (json.dumps(record, separators=(",", ":"), allow_nan=False) + "\n").encode()
        if self.bytes + len(line) > self.cap - FOOTER_RESERVE_BYTES:
            self.dropped = 1
            return False
        self._handle.write(line)
        self.bytes += len(line)
        self.records += 1
        return True

    def close(self):
        footer = {
            "footer": True,
            "truncated": self.truncated,
            "records": self.records,
            "dropped": self.dropped,
            "payloadBytes": self.bytes,
            "cap": self.cap,
        }
        line = (json.dumps(footer, separators=(",", ":")) + "\n").encode()
        if len(line) > FOOTER_RESERVE_BYTES:
            raise ValueError("The trace footer is larger than its reserve.")
        self._handle.write(line)
        self._handle.close()
        return self.bytes + len(line)
