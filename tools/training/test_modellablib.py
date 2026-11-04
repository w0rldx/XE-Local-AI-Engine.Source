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
    STEERING_KIND,
    TRACE_KIND,
    CappedTraceWriter,
    JobError,
    control_vector_bytes,
    heartbeat_counts,
    layer_kind,
    out_of_range_layers,
    scaled_to_norm,
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


def _main_failing_with(error):
    """Runs the script's real main with the trace raising, and returns its error events."""
    modellab = _load_modellab()
    modellab.torch.__version__ = "2"
    modellab.transformers.__version__ = "5"
    modellab.torch.cuda.OutOfMemoryError = MemoryError
    events = []
    with tempfile.TemporaryDirectory() as directory:
        config = os.path.join(directory, "job.json")
        with open(config, "w", encoding="utf-8") as handle:
            json.dump(VALID_JOB, handle)
        with (
            mock.patch.object(modellab, "trace", side_effect=error),
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


if __name__ == "__main__":
    for name, test in sorted(globals().items()):
        if name.startswith("test_") and callable(test):
            test()
            print(f"ok {name}")
