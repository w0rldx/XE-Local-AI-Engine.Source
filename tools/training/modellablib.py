"""Pure-python helpers for modellab.py: no torch, no transformers and no gguf, so they run without a GPU or the venv.

Run the checks with ``python3 tools/training/test_modellablib.py``.
"""

import contextlib
import hashlib
import json
import math
import os
import re
import statistics
import struct
from typing import Any, TypeGuard

# Separate from trainlib.CONTRACT_VERSION: this script and train.py change independently.
CONTRACT_VERSION = 8

TRACE_KIND = "trace"
STEERING_KIND = "steering"
SOURCE_KIND = "source"

# Both directions of the --serve protocol: one JSON object per line, the line at most this long.
MAX_FRAME_BYTES = 64 * 1024
_COMMAND_FIELDS = {"run": ("jobId", "config"), "cancel": ("jobId",), "evict": ()}
# What a trace or steering job writes; a cancelled job leaves none of it.
_OUTPUT_FIELDS = ("tracePath", "attentionPath", "resultPath", "vectorPath", "randomVectorPath")

_SOURCE_JOB_FIELDS = frozenset(("kind", "contractVersion", "configDirectory", "resultPath", "maxResultBytes"))
MAX_CONFIG_BYTES = 1024 * 1024
LABEL_CHARACTERS = 80
EXPORT_ERROR_CHARACTERS = 200
TORCH_FILE_PREFIX = "torch:"

# Room kept for the footer line, so the file including its footer can never exceed the cap.
FOOTER_RESERVE_BYTES = 160

MAX_NEW_TOKENS_LIMIT = 4096

# A trace records attention only with all four, a null one counting as absent.
_ATTENTION_FIELDS = ("attentionPath", "attentionLayers", "attentionTopKeys", "attentionByteCap")
MAX_ATTENTION_LAYERS = 8
MAX_ATTENTION_TOP_KEYS = 64
# Attention weights are stored as integers in 1/ATTENTION_WEIGHT_SCALE, rounded down.
ATTENTION_WEIGHT_SCALE = 10_000
# The model types whose attention the float32 recompute reproduces: plain causal softmax(q·kᵀ·scaling), no window,
# chunking, softcap or sink. The host's AttentionModelTypes holds the same set.
ATTENTION_FAMILIES = frozenset(("qwen3_5", "qwen3_5_text", "olmoe"))
# Config fields the pinned DynamicCache reads; set on an OLMoE config, its blocks would not all be plain full ones.
_OLMOE_ATTENTION_FIXED = ("sliding_window", "attention_chunk_size", "num_kv_shared_layers")

# A routed model records routing only within both; past either, none is recorded.
MAX_ROUTED_EXPERTS_PER_TOKEN = 16
MAX_EXPERTS = 1024
# Each routing weight is written as its bfloat16 bit pattern; the host's RoutingWeightEncoding.
ROUTING_WEIGHT_ENCODING = "bfloat16-bits"

# A trace reads every block through the output head with both, a null one counting as absent.
_LENS_FIELDS = ("lensTopTokens", "lensByteCap")
MAX_LENS_TOP_TOKENS = 20
# Text-config fields that change the logits after the output layer, with their neutral values: the batched lens rows
# would miss such a transform, so a model that sets one gets no lens.
_NEUTRAL_LOGIT_TRANSFORMS = {"final_logit_softcapping": (None,), "logits_scaling": (None, 1), "logit_scale": (None, 1)}

# A token or candidate text in result.json: its JSON-escaped UTF-8 form at most this long, cut ones ending in CUT_MARK.
# Only a result over TRACE_RESULT_BUDGET_BYTES is cut.
CANDIDATE_TEXT_BYTES = 64
CUT_MARK = "\u2026"

# A tenth below the host's ModelLabTraceSpec.MaxResultBytes (1 MiB), which refuses a larger file. The bytes are
# measured as written; the margin only absorbs a later change on either side.
TRACE_RESULT_BUDGET_BYTES = 900 * 1024

# A vector this long against the residual norm: the spike's choice from six scales.
STEERING_NORM_FACTOR = 0.5

# What llama.cpp's loader reads: a GGUF v3 file with one 1-D F32 tensor named direction.<layer>, data aligned to 32.
_GGUF_STRING_TYPE = 8
_GGUF_F32_TYPE = 0
_GGUF_ALIGNMENT = 32


class JobError(ValueError):
    """A job file the script cannot use; modellab.py reports it as the "contract" error category."""


class ProtocolError(ValueError):
    """A host command the --serve worker cannot follow: the "protocol" error category, then exit 1."""


def frame_bytes(event, token, fields):
    """One worker frame as written: a JSON line carrying the run token, at most MAX_FRAME_BYTES. An over-long
    ``message`` is cut to fit; any other frame that large is a bug and raises."""
    frame = {"event": event, "token": token, **fields}
    line = (json.dumps(frame) + "\n").encode()
    if len(line) > MAX_FRAME_BYTES and isinstance(frame.get("message"), str):
        # ASCII-escaped, so every character dropped takes at least one byte with it.
        frame["message"] = frame["message"][: max(0, len(frame["message"]) - (len(line) - MAX_FRAME_BYTES))]
        line = (json.dumps(frame) + "\n").encode()
    if len(line) > MAX_FRAME_BYTES:
        raise ValueError(f"A {event} frame is {len(line)} bytes, over the protocol's {MAX_FRAME_BYTES}.")
    return line


def write_frame(event, fields):
    """One frame carrying the spawn's XE_TRAINING_RUN_TOKEN, written to fd 1 with os.write."""
    data = frame_bytes(event, os.environ.get("XE_TRAINING_RUN_TOKEN"), fields)
    while data:  # a pipe write interrupted by a signal can be partial
        data = data[os.write(1, data) :]


def parse_command(line):
    """One host command line (bytes, as read with a limit of MAX_FRAME_BYTES + 1) as a dict with a known op."""
    if len(line) > MAX_FRAME_BYTES:
        raise ProtocolError(f"A command line is longer than {MAX_FRAME_BYTES} bytes.")
    try:
        command = json.loads(line)
    except ValueError:
        raise ProtocolError("A command line is not JSON.") from None
    if not isinstance(command, dict):
        raise ProtocolError("A command line is not a JSON object.")
    op = command.get("op")
    required = _COMMAND_FIELDS.get(op) if isinstance(op, str) else None
    if required is None:
        raise ProtocolError("A command's op is not run, cancel or evict.")
    for name in required:
        if not isinstance(command.get(name), str) or not command[name]:
            raise ProtocolError(f"A {op} command has no usable '{name}'.")
    return command


def remove_outputs(job):
    """Deletes what a job may have written so far; a file it never wrote is no error."""
    for name in _OUTPUT_FIELDS:
        if isinstance(path := job.get(name), str) and path:
            with contextlib.suppress(FileNotFoundError):
                os.remove(path)


def validate_job(job):
    """Checks every field the host fixes, before the model is loaded."""
    if not isinstance(job, dict):
        raise JobError("The job file is not a JSON object.")
    if job.get("contractVersion") != CONTRACT_VERSION:
        raise JobError("The job's contract version is not supported by this script.")
    kind = job.get("kind")
    if kind == SOURCE_KIND:
        return _validated_source_job(job)
    # The worker holds one model under this key: a later job naming another is refused, not served the wrong one.
    _require_strings(job, ("modelKey",))
    if kind == STEERING_KIND:
        return _validated_steering_job(job)
    if kind != TRACE_KIND:
        raise JobError("The job has no usable 'kind'.")
    _require_strings(job, ("checkpointPath", "prompt", "tracePath", "resultPath"))
    layers = job.get("layers")
    if not isinstance(layers, list) or not layers or not all(is_int(layer) and layer >= 0 for layer in layers):
        raise JobError("The job has no usable 'layers'.")
    if len(set(layers)) != len(layers):
        raise JobError("The job names a layer twice.")
    # A step is counted when the first listed layer's hook fires: an earlier block listed later lands a step behind.
    if layers != sorted(layers):
        raise JobError("The job's 'layers' are not in ascending order.")
    tokens = job.get("maxNewTokens")
    if not is_int(tokens) or not 1 <= tokens <= MAX_NEW_TOKENS_LIMIT:
        raise JobError("The job has no usable 'maxNewTokens'.")
    cap = job.get("traceByteCap")
    if not is_int(cap) or cap <= FOOTER_RESERVE_BYTES:
        raise JobError("The job has no usable 'traceByteCap'.")
    if not is_int(job.get("seed")):
        raise JobError("The job has no usable 'seed'.")
    _validate_attention_fields(job)
    _validate_lens_fields(job)
    routing_cap = job.get("routingByteCap")
    if routing_cap is not None and (not is_int(routing_cap) or routing_cap <= FOOTER_RESERVE_BYTES):
        raise JobError("The job has no usable 'routingByteCap'.")
    return job


def _given(job, fields, kind, together):
    """Whether the job gives a group of optional fields, refused when it gives only some."""
    given = [name for name in fields if job.get(name) is not None]
    if given and len(given) != len(fields):
        raise JobError(f"The job's {kind} fields come {together}, and it gives only {given}.")
    return bool(given)


def _validate_attention_fields(job):
    if not _given(job, _ATTENTION_FIELDS, "attention", "all four or none"):
        return
    _require_strings(job, ("attentionPath",))
    layers = job["attentionLayers"]
    usable = (
        isinstance(layers, list)
        and 1 <= len(layers) <= MAX_ATTENTION_LAYERS
        and all(is_int(layer) and layer >= 0 for layer in layers)
        and layers == sorted(set(layers))
    )
    if not usable:
        raise JobError("The job has no usable 'attentionLayers'.")
    keys = job["attentionTopKeys"]
    if not is_int(keys) or not 1 <= keys <= MAX_ATTENTION_TOP_KEYS:
        raise JobError("The job has no usable 'attentionTopKeys'.")
    cap = job["attentionByteCap"]
    if not is_int(cap) or cap <= FOOTER_RESERVE_BYTES:
        raise JobError("The job has no usable 'attentionByteCap'.")


def _validate_lens_fields(job):
    if not _given(job, _LENS_FIELDS, "lens", "both or neither"):
        return
    top = job["lensTopTokens"]
    if not is_int(top) or not 1 <= top <= MAX_LENS_TOP_TOKENS:
        raise JobError("The job has no usable 'lensTopTokens'.")
    cap = job["lensByteCap"]
    if not is_int(cap) or cap <= FOOTER_RESERVE_BYTES:
        raise JobError("The job has no usable 'lensByteCap'.")


def captures_attention(job):
    return job.get("attentionPath") is not None


def records_lens(job):
    return job.get("lensTopTokens") is not None


def lens_readable(block_count, final_norm, output_layer, text_config):
    """Whether the batched readout is the model's own for blocks 0..B-2: at least two blocks, a final norm and an
    output layer, and no logit transform after that layer. A model that fails this traces as without the lens."""
    return (
        block_count >= 2
        and final_norm is not None
        and output_layer is not None
        and all(getattr(text_config, name, None) in neutral for name, neutral in _NEUTRAL_LOGIT_TRANSFORMS.items())
    )


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
    if not is_int(layer) or layer < 1:
        raise JobError("The job has no usable 'layer'.")
    if not is_int(job.get("seed")):
        raise JobError("The job has no usable 'seed'.")
    return job


def _validated_source_job(job):
    # Exactly the contract's fields: a source job has nothing else to say, so anything more is refused, not ignored.
    if extra := sorted(set(job) - _SOURCE_JOB_FIELDS):
        raise JobError(f"The source job carries fields it may not: {extra}.")
    _require_strings(job, ("configDirectory", "resultPath"))
    cap = job.get("maxResultBytes")
    if not is_int(cap) or cap <= 0:
        raise JobError("The job has no usable 'maxResultBytes'.")
    return job


def _require_strings(job, names):
    for name in names:
        if not isinstance(job.get(name), str) or not job[name]:
            raise JobError(f"The job has no usable '{name}'.")


def is_int(value: Any) -> TypeGuard[int]:
    return isinstance(value, int) and not isinstance(value, bool)


def out_of_range_layers(layers, block_count):
    return [layer for layer in layers if layer >= block_count]


def layer_kind(block_type, model_type):
    """One letter for the trace: L for a linear-attention block, F for a full one, ? for any other or none.

    Every OLMoE block is full attention and names no block_type. Elsewhere only the literal names count.
    """
    if model_type == "olmoe":
        return "F"
    return {"full_attention": "F", "linear_attention": "L"}.get(block_type, "?")


def attention_refusal(model_type, text_config, block_count):
    """Why a trace of this model may not capture attention, or None when it may."""
    if model_type not in ATTENTION_FAMILIES:
        return "Attention capture is implemented for Qwen3.5 and OLMoE checkpoints; start the trace without it."
    if model_type != "olmoe":
        return None
    layer_types = getattr(text_config, "layer_types", None)
    if layer_types is not None and list(layer_types) != ["full_attention"] * block_count:
        return "This OLMoE config states layer types other than full attention in every block."
    if fixed := [name for name in _OLMOE_ATTENTION_FIXED if getattr(text_config, name, None) is not None]:
        return f"This OLMoE config sets {fixed}, so its attention is not plain full attention."
    return None


def routing_shape(routers):
    """The (experts per token, experts, normalized top-k) every router of (top_k, num_experts, norm_topk_prob)
    shares, or None when there is no router, they differ, or either count is past its cap: then none is recorded."""
    shapes = set(routers)
    if len(shapes) != 1:
        return None
    ((top_k, experts, normalized),) = shapes
    if not 1 <= top_k <= min(experts, MAX_ROUTED_EXPERTS_PER_TOKEN) or experts > MAX_EXPERTS:
        return None
    return top_k, experts, normalized


def routing_record(step, ids, bits):
    """One step's routing, blocks in the result's ``layers`` order: per block the chosen expert ids and the bit
    patterns of the bfloat16 weights the experts consumed, so the host decodes them exactly."""
    return {"routing": {"s": step, "x": [list(row) for row in ids], "w": [list(row) for row in bits]}}


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


def reference_norm_of(position_norms):
    """The residual length a steering vector is scaled against: the median over every position of every contrast
    prompt. Not the mean: a first position carrying a massive activation would set it, and the vector would then
    outweigh a typical token's residual."""
    return statistics.median(position_norms)


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


# How much of a failure's own text the row's reason keeps, after its type name.
FAILURE_MESSAGE_CHARACTERS = 400


def describe_failure(error):
    """The row's reason. Triton's text for a missing compiler offers remedies a user cannot reach, so it is replaced."""
    if "Failed to find C compiler" in str(error):
        return (
            "The first trace on this machine needs a C compiler, and there is no gcc or clang on the node's PATH. "
            "Install one or make it visible to the node, then start the trace again."
        )
    # Shortened, and the type kept: str(error) can be empty.
    return f"{type(error).__name__}: {str(error)[:FAILURE_MESSAGE_CHARACTERS]}"


def _rounded(value):
    # NaN and infinity have no JSON form. Null keeps the file parseable and says the value was not a finite number.
    return round(value, 3) if math.isfinite(value) else None


def _floored(value):
    # Rounded down, so the probabilities shown never sum past 1.
    return math.floor(value * 10_000) / 10_000


def _floored_logprob(value):
    # A masked (-inf) or NaN value has no JSON form: null.
    return _floored(value) if math.isfinite(value) else None


def _json_bytes(text):
    return len(json.dumps(text, ensure_ascii=False).encode()) - 2


def _written_bytes(text):
    # As CappedTraceWriter writes it: ASCII-escaped, so a CJK character takes 6 bytes and an astral one 12.
    return len(json.dumps(text)) - 2


def _cut(text, size):
    if size(text) <= CANDIDATE_TEXT_BYTES:
        return text
    while size(text + CUT_MARK) > CANDIDATE_TEXT_BYTES:
        text = text[:-1]
    return text + CUT_MARK


def cut_text(text):
    """A token text whose JSON form takes at most CANDIDATE_TEXT_BYTES, a cut one ending in an ellipsis; the id stays
    exact. Bytes, not characters: 64 control characters escape to 384 bytes, and 41 texts a step must fit the cap."""
    return _cut(text, _json_bytes)


def cut_written_text(text):
    """cut_text for a trace line: the same bound, measured in the ASCII-escaped form the trace writer writes."""
    return _cut(text, _written_bytes)


def step_candidates(ids, texts, logprobs):
    """One step's top candidates, best first. One a generation processor masked has no probability and is left out."""
    return [
        {"id": token, "text": text, "logprob": _floored(logprob)}
        for token, text, logprob in zip(ids, texts, logprobs, strict=True)
        if math.isfinite(logprob)
    ]


def scored_token(token_id, text, raw, processed):
    """A generated token with its exact log-probabilities over the whole vocabulary: raw from the model's logits, and
    processed after the checkpoint's generation-config processors. Each is (chosen logprob, ids, texts, logprobs)."""
    return {
        "id": token_id,
        "text": text,
        "logprob": _floored_logprob(raw[0]),
        "candidates": step_candidates(*raw[1:]),
        "processedLogprob": _floored_logprob(processed[0]),
        "processedCandidates": step_candidates(*processed[1:]),
    }


def check_aligned(chosen, generated, steps="candidate"):
    """Raises unless the steps a processor saw chose exactly the generated tokens, one step per token."""
    if list(chosen) != list(generated):
        raise RuntimeError(
            f"The {steps} steps ({len(chosen)}) did not choose the generated tokens ({len(generated)}): "
            "a later processor or the decoding loop changed the choice."
        )


def _trace_result_bytes(result):
    # Strict JSON, like every other writer here: the host's parser refuses NaN and infinity.
    return json.dumps(result, ensure_ascii=False, allow_nan=False).encode()


def _cut_candidate_texts(result):
    for token in result["tokens"]:
        for candidate in token["candidates"] + token["processedCandidates"]:
            candidate["text"] = cut_text(candidate["text"])


def _cut_prompt_texts(result):
    for piece in result.get("promptPieces", ()):
        piece["text"] = cut_text(piece["text"])


def _cut_token_texts(result):
    for token in result["tokens"]:
        token["text"] = cut_text(token["text"])


def _with_candidates_kept(result, candidates, kept):
    for step, (token, (raw, processed)) in enumerate(zip(result["tokens"], candidates, strict=True)):
        token["candidates"] = raw if step < kept else None
        token["processedCandidates"] = processed if step < kept else None
    result["candidateSteps"] = kept
    return _trace_result_bytes(result)


def _drop_tail_candidates(result, budget):
    # The most leading steps that fit keep their candidates: the size only grows with the steps kept, so a binary
    # search finds them. Every token keeps its id, text and logprobs.
    candidates = [(token["candidates"], token["processedCandidates"]) for token in result["tokens"]]
    low, high = 0, len(candidates) - 1
    while low < high:
        middle = (low + high + 1) // 2
        if len(_with_candidates_kept(result, candidates, middle)) <= budget:
            low = middle
        else:
            high = middle - 1
    return _with_candidates_kept(result, candidates, low)


def fitted_trace_result(result, budget=TRACE_RESULT_BUDGET_BYTES):
    """A trace's result.json bytes, texts whole unless over the budget: then candidate texts are cut, then the prompt
    pieces' texts, then token texts, then the candidates of whole steps from the end. ``candidateSteps`` says how many
    leading steps kept theirs.

    Cuts ``result`` in place. Over the budget even then, it raises: the host would refuse the file.
    """
    result["candidateSteps"] = len(result["tokens"])
    blob = _trace_result_bytes(result)
    for cut in (_cut_candidate_texts, _cut_prompt_texts, _cut_token_texts):
        if len(blob) <= budget:
            return blob
        cut(result)
        blob = _trace_result_bytes(result)
    if len(blob) > budget and result["tokens"]:
        blob = _drop_tail_candidates(result, budget)
    if len(blob) > budget:
        raise ValueError(
            f"The trace result is {len(blob)} bytes with its texts cut and its candidates dropped, "
            f"over its budget of {budget}."
        )
    return blob


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


def competition_rank(logprobs, chosen):
    """The chosen token's rank per row, ties sharing the better rank: 1 plus the count strictly above it. On tensors
    in modellab.py: ``logprobs`` [B, V], ``chosen`` [B, 1]. A NaN chosen value compares above nothing and ranks 1,
    so lens_record writes the rank only beside a finite chosen logprob."""
    return (logprobs > chosen).sum(-1) + 1


def lens_record(step, ids, logprobs, chosen, ranks, entropies, texts):
    """One step's logit lens, per block (index = block): the top ids and logprobs, the chosen token's logprob and
    rank, and the readout's entropy in nats; a rank is null exactly where its chosen logprob is. ``texts`` maps every
    id to its decoded text; the line keeps the step's distinct top ids' texts, each cut as written."""
    return {
        "lens": {
            "s": step,
            "i": [list(row) for row in ids],
            "p": [[_floored_logprob(value) for value in row] for row in logprobs],
            "c": [_floored_logprob(value) for value in chosen],
            "r": [rank if math.isfinite(value) else None for rank, value in zip(ranks, chosen, strict=True)],
            # + 0.0: a one-hot readout sums to -0.0, written 0.0.
            "e": [_rounded(value + 0.0) for value in entropies],
            "t": {str(token): cut_written_text(texts[token]) for token in sorted({t for row in ids for t in row})},
        }
    }


def _scaled_down(weight):
    if not math.isfinite(weight):
        raise JobError("An attention weight was not a finite number, so the trace's attention rows cannot be stored.")
    return math.floor(weight * ATTENTION_WEIGHT_SCALE)


def attention_record(step, layer, query_position, keys, weights):
    """The attention of the query that predicts token ``step``, at one layer: per head the kept key positions best
    first, their weights, and the rest of the row's mass. All rounded down, so a head's row never sums past the scale.
    """
    return {
        "s": step,
        "l": layer,
        "q": query_position,
        "k": [list(head) for head in keys],
        "w": [[_scaled_down(weight) for weight in head] for head in weights],
        # Clamped: a float32 softmax row can sum a hair past 1.
        "r": [max(0, _scaled_down(1 - sum(head))) for head in weights],
    }


class CappedTraceWriter:
    """JSON lines with a hard byte cap. Once a record does not fit, every later one is counted, not written."""

    def __init__(self, path, cap, footer_key=None, handle=None):
        if cap <= FOOTER_RESERVE_BYTES:
            raise ValueError("The trace byte cap is too small for its footer.")
        self.path = path
        self.cap = cap
        self.bytes = 0
        self.records = 0
        self.dropped = 0
        self._footer_key = footer_key
        self._owns_handle = handle is None
        # The directory is owner-only; the file holds statistics of the prompt's activations.
        self._handle = open(path, "wb") if handle is None else handle  # noqa: SIM115 - open until close().

    def section(self, cap, footer_key):
        """Lines on the same file under their own cap and counts, ending in a ``{footer_key: {...}}`` line; this
        writer's records and footer stay as they would be without it. Close the section before this writer."""
        return CappedTraceWriter(self.path, cap, footer_key, self._handle)

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
        counts = {
            "truncated": self.truncated,
            "records": self.records,
            "dropped": self.dropped,
            "payloadBytes": self.bytes,
            "cap": self.cap,
        }
        footer = {"footer": True, **counts} if self._footer_key is None else {self._footer_key: counts}
        line = (json.dumps(footer, separators=(",", ":")) + "\n").encode()
        if len(line) > FOOTER_RESERVE_BYTES:
            raise ValueError("The trace footer is larger than its reserve.")
        self._handle.write(line)
        if self._owns_handle:
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
