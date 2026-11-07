"""Self-checks for the Model Lab helpers. Run: ``python3 tools/training/test_modellablib.py``.

Assert-based with no framework, like test_trainlib.py, so it runs inside the training venv.
"""

import json
import math
import os
import struct
import sys
import tempfile
import threading
from unittest import mock

from modellablib import (
    CONTRACT_VERSION,
    FOOTER_RESERVE_BYTES,
    MAX_CONFIG_BYTES,
    SOURCE_KIND,
    STEERING_KIND,
    TRACE_KIND,
    CappedTraceWriter,
    JobError,
    SourceRefusalError,
    block_index_of,
    control_vector_bytes,
    discover_block_path,
    heartbeat_counts,
    in_block_path,
    layer_kind,
    layer_types_follow_cut,
    named_files,
    op_graphs,
    op_location,
    out_of_range_layers,
    read_raw_config,
    register_anchors,
    relative_source_file,
    representatives,
    sanitized_export_error,
    scaled_to_norm,
    serialized_result,
    source_file_path,
    steering_vector,
    trace_record,
    validate_job,
    vector_norm,
)


def _load_modellab():
    """The real script with torch and transformers stubbed: this file runs without either. Leaves no stub behind."""
    transformers = mock.MagicMock()
    transformers.StoppingCriteria = object
    with mock.patch.dict(sys.modules, {"torch": mock.MagicMock(), "transformers": transformers}):
        sys.modules.pop("modellab", None)
        import modellab

        sys.modules.pop("modellab", None)
    return modellab


class _ScriptedTicks(threading.Event):
    """Stands in for the heartbeat's wait: each tick runs one step, then the loop beats or not. No time passes."""

    def __init__(self, steps):
        super().__init__()
        self._steps = list(steps)

    def wait(self, timeout=None):
        if not self._steps:
            return True
        self._steps.pop(0)()
        return False


RECORD = trace_record(3, 15, "F", 3.14159, [-0.1234, 0.0, 0.1234], [1, 2], [0.5556, -0.4444])

VALID_JOB = {
    "contractVersion": CONTRACT_VERSION,
    "kind": TRACE_KIND,
    "checkpointPath": "/checkpoint",
    "prompt": "hello",
    "tracePath": "/work/trace.jsonl",
    "resultPath": "/work/result.json",
    "layers": [3, 12, 18],
    "maxNewTokens": 128,
    "traceByteCap": 262144,
    "seed": 1234,
}


def _lines(path):
    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle]


def _write(cap, count):
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, "trace.jsonl")
        writer = CappedTraceWriter(path, cap)
        for _ in range(count):
            writer.write(RECORD)
        size = writer.close()
        return size, os.path.getsize(path), _lines(path)


def test_a_trace_within_the_cap_keeps_every_record():
    size, on_disk, lines = _write(100_000, 10)
    assert size == on_disk
    assert len(lines) == 11
    footer = lines[-1]
    assert footer["footer"] is True
    assert footer["truncated"] is False
    assert footer["records"] == 10
    assert footer["dropped"] == 0
    assert lines[0] == RECORD


def test_the_cap_holds_on_deliberate_overflow_and_counts_what_it_dropped():
    for cap in (FOOTER_RESERVE_BYTES + 1, 1_000, 2_000, 20_000):
        size, on_disk, lines = _write(cap, 5_000)
        assert on_disk <= cap, f"{on_disk} > {cap}"
        assert size == on_disk
        footer = lines[-1]
        assert footer["truncated"] is True
        assert footer["records"] == len(lines) - 1
        assert footer["records"] + footer["dropped"] == 5_000


def test_a_cap_without_room_for_the_footer_is_refused():
    with tempfile.TemporaryDirectory() as directory:
        try:
            CappedTraceWriter(os.path.join(directory, "trace.jsonl"), FOOTER_RESERVE_BYTES)
        except ValueError:
            return
    raise AssertionError("A cap no larger than the footer reserve must be refused.")


def test_a_record_is_rounded_and_names_its_layer():
    assert RECORD == {
        "s": 3,
        "l": 15,
        "k": "F",
        "n": 3.142,
        "p": [-0.123, 0.0, 0.123],
        "ti": [1, 2],
        "tv": [0.556, -0.444],
    }


def test_a_non_finite_value_is_null_and_the_file_stays_strict_json():
    nan, inf = float("nan"), float("inf")
    record = trace_record(0, 6, "F", nan, [-inf, 0.25, nan], [1, 2], [inf, 0.5])
    assert record["n"] is None
    assert record["p"] == [None, 0.25, None]
    assert record["tv"] == [None, 0.5]
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, "trace.jsonl")
        writer = CappedTraceWriter(path, 10_000)
        writer.write(record)
        writer.close()

        def refuse(token):
            raise AssertionError(f"{token} is not JSON.")

        with open(path, encoding="utf-8") as handle:
            lines = [json.loads(line, parse_constant=refuse) for line in handle]
        assert lines[0] == record


def test_the_writer_refuses_a_non_finite_value_rather_than_write_invalid_json():
    with tempfile.TemporaryDirectory() as directory:
        writer = CappedTraceWriter(os.path.join(directory, "trace.jsonl"), 10_000)
        try:
            writer.write({"n": float("nan")})
        except ValueError:
            writer.close()
            return
    raise AssertionError("allow_nan must stay off.")


def test_the_heartbeat_runs_until_the_first_progress_event_then_stops_vouching_for_the_trace():
    modellab = _load_modellab()
    run = modellab._Run()
    beats = []
    input_ids = mock.MagicMock(shape=(1, 4))

    def enter(phase):
        def step():
            run.phase = phase

        return step

    def report_progress():
        modellab._ProgressAndStop(run, 3, 128)(input_ids, None)

    run._finished = _ScriptedTicks(
        [
            enter("loading"),
            # A cold compile in the first forward: tracing, with no token yet.
            enter("tracing"),
            lambda: None,
            report_progress,
            lambda: None,
            enter("saving"),
        ]
    )
    with mock.patch.object(modellab, "emit", lambda event, **fields: beats.append((event, fields.get("phase")))):
        run._beat()

    heartbeats = [phase for event, phase in beats if event == "heartbeat"]
    assert heartbeats == ["loading", "tracing", "tracing", "saving"]


def _main_failing_with(error, job=None, runner="trace"):
    """Runs the script's real main with the job's runner raising, and returns its error events."""
    modellab = _load_modellab()
    modellab.torch.__version__ = "2"
    modellab.transformers.__version__ = "5"
    modellab.torch.cuda.OutOfMemoryError = MemoryError
    events = []
    with tempfile.TemporaryDirectory() as directory:
        config = os.path.join(directory, "job.json")
        with open(config, "w", encoding="utf-8") as handle:
            json.dump(job or VALID_JOB, handle)
        with (
            mock.patch.object(modellab, runner, side_effect=error),
            mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))),
            mock.patch.object(sys, "argv", ["modellab.py", "--config", config]),
            mock.patch("os.umask"),
        ):
            assert modellab.main() == 1
    return [fields for event, fields in events if event == "error"]


def test_a_missing_compiler_says_what_to_do():
    errors = _main_failing_with(
        RuntimeError(
            "Failed to find C compiler. Please specify via CC environment variable or set triton.knobs.build.impl."
        )
    )
    assert len(errors) == 1
    message = errors[0]["message"]
    assert "no gcc or clang on the node's PATH" in message
    assert "Install one or make it visible" in message
    assert "CC environment" not in message, "A user can set neither of the remedies Triton names."


def test_any_other_failure_keeps_its_type_and_message():
    assert _main_failing_with(RuntimeError("boom"))[0]["message"] == "RuntimeError: boom"
    assert _main_failing_with(ValueError(""))[0]["message"] == "ValueError: "


def test_stop_tokens_keep_the_checkpoints_own_list():
    modellab = _load_modellab()
    tokenizer = mock.MagicMock(eos_token_id=2, unk_token_id=0)
    tokenizer.convert_tokens_to_ids.return_value = 7

    assert modellab.stop_tokens(tokenizer, [5, 6]) == [2, 5, 6, 7]
    assert modellab.stop_tokens(tokenizer, 5) == [2, 5, 7]
    assert modellab.stop_tokens(tokenizer, None) == [2, 7]


def test_a_valid_job_passes():
    assert validate_job(dict(VALID_JOB)) == VALID_JOB


def test_every_unusable_field_is_refused():
    bad_values = {
        "contractVersion": [None, CONTRACT_VERSION + 1],
        "kind": [None, "other"],
        "checkpointPath": [None, "", 1],
        "prompt": [None, ""],
        "tracePath": [None],
        "resultPath": [None],
        "layers": [None, [], ["3"], [-1], [True], [3, 3]],
        "maxNewTokens": [None, 0, 4097, "8", True],
        "traceByteCap": [None, FOOTER_RESERVE_BYTES, "9"],
        "seed": [None, "1", True],
    }
    for field, values in bad_values.items():
        for value in values:
            try:
                validate_job({**VALID_JOB, field: value})
            except JobError:
                continue
            raise AssertionError(f"{field}={value!r} must be refused.")
    try:
        validate_job([])
    except JobError:
        return
    raise AssertionError("A job that is not an object must be refused.")


def test_trace_layers_out_of_order_are_refused_and_any_ascending_set_is_kept():
    for layers in ([12, 3], [3, 18, 12], [23, 0]):
        try:
            validate_job({**VALID_JOB, "layers": layers})
        except JobError as error:
            assert "ascending" in str(error), str(error)
            continue
        raise AssertionError(f"layers={layers!r} must be refused: the tracer would file earlier blocks a step behind.")
    for layers in ([15], [0, 1, 2], list(range(64))):
        assert validate_job({**VALID_JOB, "layers": layers})["layers"] == layers


def test_layers_past_the_last_block_are_named():
    assert out_of_range_layers([3, 24, 99], 24) == [24, 99]
    assert out_of_range_layers([3, 23], 24) == []


def test_layer_kinds():
    assert layer_kind("linear_attention") == "L"
    assert layer_kind("full_attention") == "F"
    assert layer_kind(None) == "?"


VALID_STEERING_JOB = {
    "contractVersion": CONTRACT_VERSION,
    "kind": STEERING_KIND,
    "checkpointPath": "/checkpoint",
    "positivePrompts": ["good"],
    "negativePrompts": ["bad"],
    "layer": 15,
    "vectorPath": "/work/steer.gguf",
    "randomVectorPath": "/work/random.gguf",
    "resultPath": "/work/result.json",
    "seed": 1234,
}


def test_a_valid_steering_job_passes():
    assert validate_job(dict(VALID_STEERING_JOB)) == VALID_STEERING_JOB


def test_every_unusable_steering_field_is_refused():
    bad_values = {
        "checkpointPath": [None, ""],
        "vectorPath": [None, ""],
        "randomVectorPath": [None],
        "resultPath": [None],
        "positivePrompts": [None, [], [""], ["  "], [1]],
        "negativePrompts": [None, []],
        "layer": [None, 0, -1, "15", True],
        "seed": [None, "1"],
    }
    for field, values in bad_values.items():
        for value in values:
            try:
                validate_job({**VALID_STEERING_JOB, field: value})
            except JobError:
                continue
            raise AssertionError(f"{field}={value!r} must be refused.")


def test_a_steering_vector_is_the_normalised_difference_at_half_the_residual_length():
    vector, difference_norm = steering_vector([3.0, 4.0], [0.0, 0.0], reference_norm=4.0)

    assert difference_norm == 5.0
    assert math.isclose(vector_norm(vector), 2.0)
    assert math.isclose(vector[0] / vector[1], 0.75), "The direction is kept."


def test_identical_sets_have_no_direction_and_say_so():
    try:
        steering_vector([1.0, 2.0], [1.0, 2.0], reference_norm=4.0)
    except ValueError as error:
        assert "no usable length" in str(error)
        return
    raise AssertionError("A zero difference must not become a vector.")


def test_a_control_has_the_same_norm_in_its_own_direction():
    control = scaled_to_norm([1.0, -1.0, 2.0], 3.0)

    assert math.isclose(vector_norm(control), 3.0)
    assert math.isclose(control[1], -control[0])


def _read_control_vector(data):
    """The layout llama.cpp's loader and the host's check read, parsed by hand, not through the writer's helpers."""
    assert data[:4] == b"GGUF"
    version, tensors, keys = struct.unpack_from("<IQQ", data, 4)
    position = 24

    def string():
        nonlocal position
        (length,) = struct.unpack_from("<Q", data, position)
        position += 8 + length
        return data[position - length : position].decode()

    assert (version, tensors, keys) == (3, 1, 1)
    assert string() == "general.architecture"
    assert struct.unpack_from("<I", data, position) == (8,)
    position += 4
    assert string() == "controlvector"
    name = string()
    dimensions, count, ggml_type, offset = struct.unpack_from("<IQIQ", data, position)
    position += 24
    assert (dimensions, ggml_type, offset) == (1, 0, 0)
    start = position + (-position % 32)
    assert len(data) == start + count * 4, "Nothing may follow the one tensor."
    return name, list(struct.unpack_from(f"<{count}f", data, start))


def test_a_control_vector_file_has_the_layout_the_loader_reads():
    name, values = _read_control_vector(control_vector_bytes(19, [0.5, -0.25, 1.0]))

    assert name == "direction.19"
    assert values == [0.5, -0.25, 1.0]


def test_a_vector_the_runtime_would_drop_or_poison_is_not_written():
    for layer, values in ((0, [1.0]), (5, []), (5, [0.0, 0.0]), (5, [1.0, float("nan")]), (5, [float("inf")])):
        try:
            control_vector_bytes(layer, values)
        except ValueError:
            continue
        raise AssertionError(f"layer {layer} with {values} must be refused.")


def test_deriving_vouches_like_tracing_once_prompts_flow():
    assert heartbeat_counts("deriving", progress_seen=False)
    assert not heartbeat_counts("deriving", progress_seen=True)
    assert heartbeat_counts("saving", progress_seen=True)


# --- Source job ---

VALID_SOURCE_JOB = {
    "kind": SOURCE_KIND,
    "contractVersion": CONTRACT_VERSION,
    "configDirectory": "/work/config",
    "resultPath": "/work/result.json",
    "maxResultBytes": 1048576,
}


def test_the_contract_is_version_3_and_a_version_2_job_is_refused():
    assert CONTRACT_VERSION == 3
    for job in (VALID_JOB, VALID_STEERING_JOB, VALID_SOURCE_JOB):
        try:
            validate_job({**job, "contractVersion": 2})
        except JobError:
            continue
        raise AssertionError(f"A version 2 {job['kind']} job must be refused.")


def test_a_valid_source_job_passes():
    assert validate_job(dict(VALID_SOURCE_JOB)) == VALID_SOURCE_JOB


def test_every_unusable_source_field_is_refused():
    bad_values = {
        "configDirectory": [None, "", 1],
        "resultPath": [None, ""],
        "maxResultBytes": [None, 0, -1, "1024", True, 1.5],
    }
    for field, values in bad_values.items():
        for value in values:
            try:
                validate_job({**VALID_SOURCE_JOB, field: value})
            except JobError:
                continue
            raise AssertionError(f"{field}={value!r} must be refused.")


def test_a_source_job_with_any_other_field_is_refused():
    for extra in ({"trustRemoteCode": True}, {"checkpointPath": "/checkpoint"}, {"script": "x.py"}):
        try:
            validate_job({**VALID_SOURCE_JOB, **extra})
        except JobError as error:
            assert next(iter(extra)) in str(error)
            continue
        raise AssertionError(f"{extra} must be refused, not ignored.")


def _config_dir(directory, content):
    with open(os.path.join(directory, "config.json"), "wb") as handle:
        handle.write(content if isinstance(content, bytes) else json.dumps(content).encode())
    return directory


def _refusal(action):
    try:
        action()
    except SourceRefusalError as refusal:
        return refusal.category
    raise AssertionError("A refusal was expected.")


def test_a_plain_config_is_read_as_json():
    with tempfile.TemporaryDirectory() as directory:
        raw = {"model_type": "llama", "text_config": {"model_type": "llama"}}
        assert read_raw_config(_config_dir(directory, raw)) == raw


def test_a_config_naming_repo_code_is_refused_at_any_depth():
    configs = (
        {"model_type": "nomic_bert", "auto_map": {"AutoConfig": "configuration_hf_nomic_bert.NomicBertConfig"}},
        {"model_type": "qwen3_5", "text_config": {"model_type": "qwen3_5_text", "auto_map": {}}},
        {"model_type": "x", "sub_configs": [{"vision_config": {"auto_map": {}}}]},
    )
    for raw in configs:
        with tempfile.TemporaryDirectory() as directory:
            assert _refusal(lambda directory=directory, raw=raw: read_raw_config(_config_dir(directory, raw))) == (
                "remote_code"
            ), raw


def test_a_missing_broken_or_oversized_config_is_unreadable():
    contents = (b"{not json", b"[1, 2]", b"\xff\xfe", b"{" + b" " * MAX_CONFIG_BYTES + b"}")
    for content in contents:
        with tempfile.TemporaryDirectory() as directory:
            assert (
                _refusal(lambda directory=directory, content=content: read_raw_config(_config_dir(directory, content)))
                == "config_unreadable"
            )
    with tempfile.TemporaryDirectory() as directory:
        assert _refusal(lambda: read_raw_config(directory)) == "config_unreadable"


def test_a_config_of_exactly_the_cap_is_read():
    with tempfile.TemporaryDirectory() as directory:
        content = b'{"model_type": "llama"' + b" " * (MAX_CONFIG_BYTES - 23) + b"}"
        assert len(content) == MAX_CONFIG_BYTES
        assert read_raw_config(_config_dir(directory, content)) == {"model_type": "llama"}


SITE = "/site"
TF_ROOT = f"{SITE}/transformers"
TORCH_ROOT = f"{SITE}/torch"


def test_files_are_named_relative_to_their_package_and_never_absolute():
    assert relative_source_file(f"{TF_ROOT}/models/llama/modeling_llama.py", TF_ROOT, TORCH_ROOT) == (
        "models/llama/modeling_llama.py"
    )
    assert relative_source_file(f"{TORCH_ROOT}/nn/modules/linear.py", TF_ROOT, TORCH_ROOT) == (
        "torch:nn/modules/linear.py"
    )
    for outside in (f"{SITE}/transformers_evil/x.py", f"{SITE}/other/x.py", f"{TF_ROOT}/../evil.py", TF_ROOT):
        assert relative_source_file(outside, TF_ROOT, TORCH_ROOT) is None, outside


def test_a_relative_name_maps_back_to_its_installed_file():
    for name in ("models/llama/modeling_llama.py", "torch:nn/modules/linear.py"):
        path = source_file_path(name, TF_ROOT, TORCH_ROOT)
        assert relative_source_file(path, TF_ROOT, TORCH_ROOT) == name


def _relative(path):
    return relative_source_file(path, TF_ROOT, TORCH_ROOT)


def _trace(*frames):
    return "\n".join(f'  File "{file}", line {line}, in {function}\n    code' for file, line, function in frames)


def test_an_op_lands_on_its_deepest_modeling_line():
    stack = _trace(
        (f"{TF_ROOT}/models/llama/modeling_llama.py", 300, "forward"),
        (f"{TF_ROOT}/models/llama/modeling_llama.py", 120, "apply_rotary_pos_emb"),
        (f"{TF_ROOT}/integrations/sdpa_attention.py", 80, "sdpa_attention_forward"),
        (f"{TORCH_ROOT}/nn/functional.py", 10, "linear"),
    )
    assert op_location(stack, _relative) == ("models/llama/modeling_llama.py", 120)


def test_an_op_outside_modeling_files_lands_on_its_deepest_transformers_line_or_nowhere():
    stack = _trace((f"{TF_ROOT}/masking_utils.py", 40, "create_causal_mask"), (f"{TORCH_ROOT}/x.py", 1, "f"))
    assert op_location(stack, _relative) == ("masking_utils.py", 40)
    assert op_location(_trace((f"{TORCH_ROOT}/x.py", 1, "f"), ("/elsewhere/y.py", 2, "g")), _relative) == (None, None)
    assert op_location(None, _relative) == (None, None)


def test_the_block_list_is_the_outermost_longest_then_shallowest():
    assert discover_block_path([("model.layers", 64), ("model.layers.3.mlp.experts", 128)]) == "model.layers"
    assert discover_block_path([("backbone.layers", 4)]) == "backbone.layers"
    assert discover_block_path([("model.vision.blocks", 12), ("model.layers", 24)]) == "model.layers"
    assert discover_block_path([("a.b.layers", 8), ("layers", 8)]) == "layers"
    assert discover_block_path([]) is None


def test_module_paths_split_into_block_and_in_block_path():
    assert block_index_of("model.layers.1", "model.layers") == 1
    assert block_index_of("model.layers.10.self_attn.q_proj", "model.layers") == 10
    assert block_index_of("model.layers", "model.layers") is None
    assert block_index_of("model.layers_x.1", "model.layers") is None
    assert block_index_of("model.norm", "model.layers") is None
    assert in_block_path("model.layers.10.self_attn.q_proj", "model.layers", 10) == "self_attn.q_proj"
    assert in_block_path("model.layers.10", "model.layers", 10) == ""
    assert in_block_path("model.norm", "model.layers", None) == "model.norm"


def test_one_representative_per_structure_and_every_block_maps_to_its_own():
    chosen, position_of = representatives(["linear", "full", "linear", "linear", "full", "moe"])
    assert chosen == [0, 1, 5]
    assert position_of == [0, 1, 0, 0, 1, 2]


def test_layer_types_that_cannot_follow_the_cut_force_full_depth():
    full = ["linear_attention", "linear_attention", "full_attention", "linear_attention"]
    assert layer_types_follow_cut(full, [0, 2], ["linear_attention", "full_attention"])
    # jamba: a read-only layer_types derived from the period reads back as the full list, or recomputed for 2 layers.
    assert not layer_types_follow_cut(full, [0, 2], full)
    assert not layer_types_follow_cut(full, [0, 2], ["linear_attention", "linear_attention"])
    assert not layer_types_follow_cut(full, [0, 2], None)
    assert layer_types_follow_cut([], [0], None), "A model without layer_types has nothing to follow."


def test_anchors_register_every_function_or_fall_back_to_call_site_lines():
    registered = []
    assert register_anchors(registered.append, ["rotate_half", "apply_rotary_pos_emb"]) is True
    assert registered == ["rotate_half", "apply_rotary_pos_emb"]
    assert register_anchors(None, ["rotate_half"]) is False, "The private hook is gone."

    def broken(_function):
        raise TypeError("the hook changed its signature")

    assert register_anchors(broken, ["rotate_half"]) is False


MODELING = "models/llama/modeling_llama.py"
LINES = {(MODELING, 10): "    hidden = self.norm(x)   ", (MODELING, 20): "    return " + "y" * 100}


def _op(path, file, line, op, *inputs):
    return {"path": path, "file": file, "line": line, "op": op, "inputs": list(inputs)}


def _two_block_ops():
    ops = [_op("model.embed_tokens", "torch:nn/modules/sparse.py", 5, "embedding")]
    for block in (0, 1):
        start = len(ops)
        ops += [
            _op(f"model.layers.{block}.input_layernorm", MODELING, 10, "pow", start - 1),
            _op(f"model.layers.{block}.input_layernorm", MODELING, 10, "mean", start),
            _op(f"model.layers.{block}.mlp", MODELING, 20, "mm", start + 1),
        ]
    ops.append(_op("model.rotary_emb", None, None, "wrap_with_set_grad_enabled"))
    ops.append(_op("lm_head", MODELING, 20, "mm", len(ops) - 2))
    return ops


def test_ops_group_into_one_deduplicated_graph_per_block_structure():
    graphs, graph_of = op_graphs(_two_block_ops(), "model.layers", lambda file, line: LINES[(file, line)])

    assert graph_of[0] == graph_of[1], "Identical blocks share one graph."
    assert graph_of[None] != graph_of[0]
    assert [graph["graphId"] for graph in graphs] == [graph_of[None], graph_of[0]], "Outside first, then blocks."
    block = graphs[1]
    assert block["nodes"] == [
        {
            "id": f"input_layernorm@{MODELING}:10",
            "modulePath": "input_layernorm",
            "file": MODELING,
            "line": 10,
            "opCount": 2,
            "label": "hidden = self.norm(x)",
        },
        {
            "id": f"mlp@{MODELING}:20",
            "modulePath": "mlp",
            "file": MODELING,
            "line": 20,
            "opCount": 1,
            "label": "return " + "y" * 73,
        },
    ]
    # The edge from the embedding crosses into the block, and the one inside a node is no edge: neither is kept.
    assert block["edges"] == [[f"input_layernorm@{MODELING}:10", f"mlp@{MODELING}:20"]]

    outside = {node["id"]: node for node in graphs[0]["nodes"]}
    embedding = "model.embed_tokens@torch:nn/modules/sparse.py:5"
    assert set(outside) == {embedding, "model.rotary_emb@None", f"lm_head@{MODELING}:20"}
    assert outside[embedding]["label"] == "", "Torch lines carry no label."
    assert outside["model.rotary_emb@None"]["file"] is None
    assert outside["model.rotary_emb@None"]["label"] == ""
    assert graphs[0]["edges"] == [], "The rotary op feeds nothing here and lm_head reads a block op."


def test_one_module_on_the_same_line_of_two_files_is_two_nodes():
    helper = "integrations/sdpa_attention.py"
    ops = [
        _op("model.layers.0.self_attn", MODELING, 10, "mm"),
        _op("model.layers.0.self_attn", helper, 10, "sdpa", 0),
        _op("model.layers.0.self_attn", MODELING, 10, "add", 1),
    ]
    graphs, graph_of = op_graphs(ops, "model.layers", lambda file, line: LINES.get((file, line), "x"))

    graph = next(graph for graph in graphs if graph["graphId"] == graph_of[0])
    own, called = f"self_attn@{MODELING}:10", f"self_attn@{helper}:10"
    assert [node["id"] for node in graph["nodes"]] == [called, own]
    assert [node["opCount"] for node in graph["nodes"]] == [1, 2]
    # Each direction is its own edge, not a self-loop of one merged node.
    assert graph["edges"] == [[called, own], [own, called]]


def test_blocks_with_different_ops_get_different_graphs():
    ops = _two_block_ops()
    ops[3]["op"] = "rsqrt"  # block 0's mlp
    _, graph_of = op_graphs(ops, "model.layers", lambda file, line: LINES[(file, line)])
    assert graph_of[0] != graph_of[1]


def test_an_export_error_keeps_its_type_and_first_line_without_absolute_paths():
    error = RuntimeError(
        'Could not trace "/home/someone/.venv/lib/python3.14/site-packages/transformers/x.py": bad shape\nsecond line'
    )
    message = sanitized_export_error(error)
    assert message == 'RuntimeError: Could not trace "<path>": bad shape'
    assert sanitized_export_error(ValueError("")) == "ValueError: "
    assert sanitized_export_error(ValueError("ratio 1/2 at /tmp/x")) == "ValueError: ratio 1/2 at <path>"
    long = sanitized_export_error(KeyError("k" * 500))
    assert len(long) <= len("KeyError: ") + 200


def test_every_named_file_is_listed_once():
    blocks = [{"modules": [{"file": MODELING}, {"file": None}]}]
    outside = [{"file": "torch:nn/modules/linear.py"}]
    graphs = [{"nodes": [{"file": "activations.py"}, {"file": MODELING}, {"file": None}]}]
    assert named_files(blocks, outside, graphs) == ["activations.py", MODELING, "torch:nn/modules/linear.py"]


def test_a_result_over_the_cap_is_refused():
    result = {"blocks": ["x" * 100]}
    blob = serialized_result(result, 1_000)
    assert json.loads(blob) == result
    assert _refusal(lambda: serialized_result(result, len(blob) - 1)) == "result_too_large"


def test_a_source_refusal_reaches_the_host_under_its_own_category():
    errors = _main_failing_with(
        SourceRefusalError("remote_code", "config.json names repository code."), VALID_SOURCE_JOB, "source"
    )
    assert errors == [{"category": "remote_code", "message": "config.json names repository code."}]


if __name__ == "__main__":
    for name, test in sorted(globals().items()):
        if name.startswith("test_") and callable(test):
            test()
            print(f"ok {name}")
