"""Pure-python helpers for modellab.py: no torch, no transformers and no gguf, so they run without a GPU or the venv.

Run the checks with ``python3 tools/training/test_modellablib.py``.
"""

import hashlib
import json
import math
import os
import re
import struct
from typing import Any, TypeGuard

# Separate from trainlib.CONTRACT_VERSION: this script and train.py change independently.
CONTRACT_VERSION = 3

TRACE_KIND = "trace"
STEERING_KIND = "steering"
SOURCE_KIND = "source"

_SOURCE_JOB_FIELDS = frozenset(("kind", "contractVersion", "configDirectory", "resultPath", "maxResultBytes"))
MAX_CONFIG_BYTES = 1024 * 1024
LABEL_CHARACTERS = 80
EXPORT_ERROR_CHARACTERS = 200
TORCH_FILE_PREFIX = "torch:"

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
    if kind == SOURCE_KIND:
        return _validated_source_job(job)
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


def _validated_source_job(job):
    # Exactly the contract's fields: a source job has nothing else to say, so anything more is refused, not ignored.
    if extra := sorted(set(job) - _SOURCE_JOB_FIELDS):
        raise JobError(f"The source job carries fields it may not: {extra}.")
    _require_strings(job, ("configDirectory", "resultPath"))
    cap = job.get("maxResultBytes")
    if not _is_int(cap) or cap <= 0:
        raise JobError("The job has no usable 'maxResultBytes'.")
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


# --- Source job: the model's PyTorch module tree and op graph, from its config.json alone. ---


class SourceRefusalError(Exception):
    """A config the source job will not index. ``category`` is the stdio error event's category."""

    def __init__(self, category, message):
        super().__init__(message)
        self.category = category


def read_raw_config(directory):
    """config.json as plain JSON, refused before transformers sees it when it names repo code (``auto_map``)."""
    try:
        with open(os.path.join(directory, "config.json"), "rb") as handle:
            data = handle.read(MAX_CONFIG_BYTES + 1)
        raw = json.loads(data) if len(data) <= MAX_CONFIG_BYTES else None
    except OSError, ValueError:
        raw = None
    if not isinstance(raw, dict):
        raise SourceRefusalError(
            "config_unreadable", "config.json is missing, not a JSON object, or larger than 1 MiB."
        )
    if names_remote_code(raw):
        raise SourceRefusalError("remote_code", "config.json names repository code (auto_map), which is never run.")
    return raw


def names_remote_code(node):
    """Whether ``auto_map`` appears anywhere: top level and text_config, and any deeper sub-config too."""
    if isinstance(node, dict):
        return "auto_map" in node or any(names_remote_code(value) for value in node.values())
    return isinstance(node, list) and any(names_remote_code(value) for value in node)


def relative_source_file(path, transformers_root, torch_root):
    """A file as the result names it, relative to the transformers or (prefixed) torch package; None for others."""
    path = os.path.realpath(path)
    for root, prefix in ((transformers_root, ""), (torch_root, TORCH_FILE_PREFIX)):
        root = os.path.realpath(root)
        if path.startswith(root + os.sep):
            return prefix + os.path.relpath(path, root)
    return None


def source_file_path(file, transformers_root, torch_root):
    """The installed file a result's relative name stands for."""
    if file.startswith(TORCH_FILE_PREFIX):
        return os.path.join(torch_root, file[len(TORCH_FILE_PREFIX) :])
    return os.path.join(transformers_root, file)


_FRAME = re.compile(r'File "([^"]+)", line (\d+), in (\w+)')
_MODELING_FILE = re.compile(r"models/[^/]+/modeling_")


def op_location(stack_trace, relative):
    """(file, line) of one exported op: its deepest modeling-file frame, else its deepest transformers frame."""
    frames = [(relative(file), int(line)) for file, line, _ in _FRAME.findall(stack_trace or "")]
    in_transformers = [frame for frame in frames if frame[0] and not frame[0].startswith(TORCH_FILE_PREFIX)]
    in_modeling = [frame for frame in in_transformers if _MODELING_FILE.match(frame[0])]
    return (in_modeling or in_transformers or [(None, None)])[-1]


def discover_block_path(module_lists):
    """The block list among (name, length) of the non-empty ModuleLists whose items all have children.

    The outermost one, longest, then shallowest: model.layers, backbone.layers. None when there is none.
    """
    names = [name for name, _ in module_lists]
    outer = [(name, length) for name, length in module_lists if not any(name.startswith(o + ".") for o in names)]
    if not outer:
        return None
    return max(outer, key=lambda entry: (entry[1], -entry[0].count(".")))[0]


def block_index_of(path, block_path):
    """The block a module path lies in, or None outside every block."""
    match = re.match(rf"{re.escape(block_path)}\.(\d+)(?:\.|$)", path)
    return int(match.group(1)) if match else None


def in_block_path(path, block_path, index):
    """A module path relative to its block ("" for the block itself); outside paths stay absolute."""
    if index is None:
        return path
    prefix = f"{block_path}.{index}"
    if path == prefix:
        return ""
    return path[len(prefix) + 1 :] if path.startswith(prefix + ".") else path


def representatives(keys):
    """One block per distinct structure key: (the representatives' indices, each block's representative position)."""
    first = {}
    for index, key in enumerate(keys):
        first.setdefault(key, index)
    position = {key: slot for slot, key in enumerate(first)}
    return list(first.values()), [position[key] for key in keys]


def layer_types_follow_cut(full_types, representative_indices, read_back):
    """Whether the patched config's layer_types are the representatives' own. A derived layer_types (jamba) is not."""
    return not full_types or list(read_back or []) == [full_types[i] for i in representative_indices]


def register_anchors(register, functions):
    """Anchors the modeling module's free functions so export records lines inside them.

    ``register`` is torch's private hook, None when it is gone. Any failure means call-site lines: returns False.
    """
    if register is None:
        return False
    try:
        for function in functions:
            register(function)
    except Exception:
        return False
    return True


def trim_label(text):
    return text.strip()[:LABEL_CHARACTERS]


def op_graphs(ops, block_path, source_line):
    """Groups exported ops by (module path, file, line) into one graph per block, deduplicated by signature.

    ``ops`` are {"path", "file", "line", "op", "inputs": [indices of earlier ops]}; ``source_line(file, line)`` is
    the line's text. Returns (graphs, {block position or None for outside: graphId}).
    """
    keys = [(op["path"], op["file"], op["line"]) for op in ops]
    buckets = {}
    for op, key in zip(ops, keys, strict=True):
        index = block_index_of(key[0], block_path)
        group = buckets.setdefault(index, {"count": {}, "ops": {}, "edges": set()})
        group["count"][key] = group["count"].get(key, 0) + 1
        group["ops"].setdefault(key, []).append(op["op"])
        for source in op["inputs"]:
            if keys[source] != key and block_index_of(keys[source][0], block_path) == index:
                group["edges"].add((keys[source], key))

    graphs, graph_of = {}, {}
    # The outside graph first, then blocks in order.
    for index in sorted(buckets, key=lambda value: -1 if value is None else value):
        group = buckets[index]
        ordered = sorted(group["count"], key=lambda key: (key[1] or "", key[2] or 0, key[0]))
        # The file is part of the id: one module can run the same line number of two files (its own and a helper's).
        node_id = {
            key: in_block_path(key[0], block_path, index) + ("@None" if key[1] is None else f"@{key[1]}:{key[2]}")
            for key in ordered
        }
        nodes = [
            {
                "id": node_id[key],
                "modulePath": in_block_path(key[0], block_path, index),
                "file": key[1],
                "line": key[2],
                "opCount": group["count"][key],
                "label": _label(source_line, key[1], key[2]),
            }
            for key in ordered
        ]
        edges = sorted([node_id[a], node_id[b]] for a, b in group["edges"])
        signature = json.dumps(
            [
                [
                    (node["id"], node["file"], sorted(group["ops"][key]))
                    for node, key in zip(nodes, ordered, strict=True)
                ],
                edges,
            ]
        )
        graph_id = hashlib.sha256(signature.encode()).hexdigest()[:16]
        graphs.setdefault(graph_id, {"graphId": graph_id, "nodes": nodes, "edges": edges})
        graph_of[index] = graph_id
    return list(graphs.values()), graph_of


def _label(source_line, file, line):
    # Only transformers lines: torch's own lines say nothing about the model.
    if not file or file.startswith(TORCH_FILE_PREFIX) or line is None:
        return ""
    return trim_label(source_line(file, line))


_ABSOLUTE_PATH = re.compile(r"(?<![\w.~-])/[^\s'\"()\[\]{},;:]+")


def sanitized_export_error(error):
    """One line for the result: the type and the first message line, absolute paths replaced, at most 200 chars."""
    first = (str(error).splitlines() or [""])[0]
    return f"{type(error).__name__}: {_ABSOLUTE_PATH.sub('<path>', first)[:EXPORT_ERROR_CHARACTERS]}"


def named_files(blocks, outside, graphs):
    """Every file a module or a node names, once, sorted."""
    modules = outside + [module for block in blocks for module in block["modules"]]
    nodes = [node for graph in graphs for node in graph["nodes"]]
    return sorted({entry["file"] for entry in modules + nodes if entry["file"]})


def serialized_result(result, cap):
    blob = json.dumps(result, separators=(",", ":"), allow_nan=False).encode()
    if len(blob) > cap:
        raise SourceRefusalError("result_too_large", f"The index is {len(blob)} bytes, over the job's cap of {cap}.")
    return blob
