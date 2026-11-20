"""Self-checks for the Model Lab helpers. Run: ``python3 tools/training/test_modellablib.py``.

Assert-based with no framework, like test_trainlib.py, so it runs inside the training venv.
"""

import contextlib
import json
import math
import os
import struct
import sys
import tempfile
import threading
from unittest import mock

from modellablib import (
    ATTENTION_WEIGHT_SCALE,
    CANDIDATE_TEXT_BYTES,
    CONTRACT_VERSION,
    CUT_MARK,
    FOOTER_RESERVE_BYTES,
    MAX_CONFIG_BYTES,
    MAX_FRAME_BYTES,
    MAX_LENS_TOP_TOKENS,
    SOURCE_KIND,
    STEERING_KIND,
    TRACE_KIND,
    TRACE_RESULT_BUDGET_BYTES,
    CappedTraceWriter,
    JobError,
    ProtocolError,
    SourceRefusalError,
    attention_record,
    block_index_of,
    captures_attention,
    check_aligned,
    competition_rank,
    control_vector_bytes,
    cut_text,
    cut_written_text,
    discover_block_path,
    fitted_trace_result,
    frame_bytes,
    heartbeat_counts,
    in_block_path,
    layer_kind,
    layer_types_follow_cut,
    lens_readable,
    lens_record,
    named_files,
    op_graphs,
    op_location,
    out_of_range_layers,
    parse_command,
    read_raw_config,
    records_lens,
    register_anchors,
    relative_source_file,
    remove_outputs,
    representatives,
    sanitized_export_error,
    scaled_to_norm,
    scored_token,
    serialized_result,
    source_file_path,
    steering_vector,
    step_candidates,
    trace_record,
    validate_job,
    vector_norm,
)


def _load_modellab():
    """The real script with torch and transformers stubbed: this file runs without either. Leaves no stub behind."""
    transformers = mock.MagicMock()
    transformers.StoppingCriteria = object
    transformers.LogitsProcessor = object
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
    "modelKey": "artifact-1:rev-2:bf16:6",
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


def test_a_file_with_no_record_still_ends_in_its_footer():
    size, on_disk, lines = _write(1_000, 0)
    assert size == on_disk
    assert lines == [{"footer": True, "truncated": False, "records": 0, "dropped": 0, "payloadBytes": 0, "cap": 1_000}]


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


def test_a_scored_token_carries_raw_and_processed_candidates_rounded_down():
    raw = (-0.123456, [7, 3], ["a", "b"], [-0.123456, -2.5])
    processed = (-0.000001, [7, 5], ["a", "c"], [-0.000001, -14.0])
    assert scored_token(7, "a", raw, processed) == {
        "id": 7,
        "text": "a",
        "logprob": -0.1235,
        "candidates": [{"id": 7, "text": "a", "logprob": -0.1235}, {"id": 3, "text": "b", "logprob": -2.5}],
        "processedLogprob": -0.0001,
        "processedCandidates": [{"id": 7, "text": "a", "logprob": -0.0001}, {"id": 5, "text": "c", "logprob": -14.0}],
    }


def test_rounded_down_probabilities_never_sum_past_one():
    logprobs = [math.log(p) for p in (0.99999, 0.000004, 0.000003, 0.000002, 0.000001)]
    shown = step_candidates(range(5), ["t"] * 5, logprobs)
    assert sum(math.exp(candidate["logprob"]) for candidate in shown) <= 1


def test_a_masked_candidate_is_left_out_and_a_masked_choice_is_null():
    inf, nan = float("inf"), float("nan")
    assert step_candidates([1, 2, 3], ["x", "y", "z"], [-0.5, -inf, nan]) == [{"id": 1, "text": "x", "logprob": -0.5}]
    token = scored_token(2, "y", (-inf, [], [], []), (nan, [], [], []))
    assert token["logprob"] is None
    assert token["processedLogprob"] is None
    json.dumps(token, allow_nan=False)


def test_a_long_text_is_cut_by_its_escaped_bytes_and_says_so():
    assert cut_text("short") == "short"
    for text in ("a" * 200, "\u00e4" * 200, "\x01" * 200, "\U0001f600" * 200):
        cut = cut_text(text)
        assert cut.endswith(CUT_MARK)
        assert text.startswith(cut[: -len(CUT_MARK)])
        assert len(json.dumps(cut, ensure_ascii=False).encode()) - 2 <= CANDIDATE_TEXT_BYTES


def _trace_result(text):
    """ModelLabTraceSpec.MaxNewTokens steps, ids and logprobs at full width, every text the given one."""
    step = (-12.345678, [151_000 + i for i in range(20)], [text] * 20, [-12.345678 - i for i in range(20)])
    return {
        "promptTokens": 4096,
        "tokens": [scored_token(151_643, text, step, step) for _ in range(128)],
        "text": text * 128,
        "seconds": 12.345,
    }


def _texts(result):
    tokens = result["tokens"]
    candidates = [c["text"] for token in tokens for c in token["candidates"] + token["processedCandidates"]]
    return [token["text"] for token in tokens], candidates


def test_a_normal_result_keeps_every_text_whole():
    long_word = "a" * 100  # over CANDIDATE_TEXT_BYTES, still whole when the result fits
    result = _trace_result(long_word)
    blob = fitted_trace_result(result)
    assert blob == json.dumps(result, ensure_ascii=False).encode()
    token_texts, candidate_texts = _texts(json.loads(blob))
    assert set(token_texts) == set(candidate_texts) == {long_word}


def test_the_worst_result_is_cut_and_fits_the_hosts_result_cap():
    # Every token and candidate text a control character run (the costliest escape): about 6 MiB whole. Cut, the
    # candidate texts end in the mark, the ids stay exact, and the file fits ModelLabTraceSpec.MaxResultBytes.
    # The full decoded "text" field is the generation itself and is not cut.
    max_result_bytes, text = 1024 * 1024, "\x01" * 200
    result = _trace_result(text)
    assert len(json.dumps(result, ensure_ascii=False).encode()) > max_result_bytes
    blob = fitted_trace_result(result)
    assert len(blob) <= TRACE_RESULT_BUDGET_BYTES < max_result_bytes, len(blob)
    written = json.loads(blob)
    _, candidate_texts = _texts(written)
    assert all(cut.endswith(CUT_MARK) and text.startswith(cut[:-1]) for cut in candidate_texts)
    assert written["text"] == text * 128
    assert written["tokens"][0]["candidates"][19]["id"] == 151_019
    assert written["tokens"][0]["id"] == 151_643


def test_candidate_texts_are_cut_before_token_texts():
    text = "b" * 200
    whole = len(json.dumps(_trace_result(text), ensure_ascii=False).encode())
    result = _trace_result(text)
    candidates_cut = len(fitted_trace_result(result, budget=whole // 2))
    token_texts, candidate_texts = _texts(result)
    assert set(token_texts) == {text}
    assert all(candidate.endswith(CUT_MARK) for candidate in candidate_texts)

    result = _trace_result(text)
    assert len(fitted_trace_result(result, budget=candidates_cut - 1)) < candidates_cut
    token_texts, _ = _texts(result)
    assert all(token.endswith(CUT_MARK) for token in token_texts)
    assert result["text"] == text * 128


def test_prompt_piece_texts_are_cut_after_candidate_texts_and_before_token_texts():
    text = "d" * 200

    def with_pieces():
        return {**_trace_result(text), "promptPieces": [{"id": 9, "text": text} for _ in range(512)]}

    def cut(result):
        token_texts, candidate_texts = _texts(result)
        pieces = [piece["text"] for piece in result["promptPieces"]]
        return [all(t.endswith(CUT_MARK) for t in texts) for texts in (candidate_texts, pieces, token_texts)]

    whole = len(json.dumps(with_pieces(), ensure_ascii=False).encode())
    result = with_pieces()
    candidates_cut = len(fitted_trace_result(result, budget=whole - 1))
    assert cut(result) == [True, False, False]

    result = with_pieces()
    pieces_cut = len(fitted_trace_result(result, budget=candidates_cut - 1))
    assert cut(result) == [True, True, False]
    assert result["promptPieces"][0]["id"] == 9

    result = with_pieces()
    fitted_trace_result(result, budget=pieces_cut - 1)
    assert cut(result) == [True, True, True]


def test_a_result_over_its_budget_even_cut_is_refused():
    try:
        fitted_trace_result(_trace_result("c" * 200), budget=1_000)
    except ValueError as error:
        assert "over its budget of 1000" in str(error)
    else:
        raise AssertionError("A result that cannot fit must not be written.")


def test_the_candidate_steps_must_choose_the_generated_tokens():
    check_aligned([5, 6, 7], [5, 6, 7])
    for chosen in ([5, 6], [5, 6, 7, 8], [5, 9, 7]):
        try:
            check_aligned(chosen, [5, 6, 7])
        except RuntimeError:
            continue
        raise AssertionError(f"{chosen} passed as aligned.")


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
        modellab._Progress(run, 3, 128)(input_ids, None)

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


def test_sigterm_while_tracing_exits_at_once_as_cancelled():
    modellab = _load_modellab()
    run = modellab._Run()
    run.phase = "tracing"
    events = []
    with (
        mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))),
        mock.patch("os._exit") as exit_now,
    ):
        run.on_sigterm(15, None)

    assert events == [("done", {"cancelled": True})]
    exit_now.assert_called_once_with(modellab.CANCELLED_EXIT_CODE)


def test_a_frame_is_one_json_line_with_the_token_and_its_fields():
    run_id = "run-1"
    line = frame_bytes("progress", run_id, {"jobId": "j1", "step": 3})
    assert line.endswith(b"\n")
    assert line.count(b"\n") == 1
    assert json.loads(line) == {"event": "progress", "token": run_id, "jobId": "j1", "step": 3}


def test_an_over_long_message_is_cut_to_the_frame_cap_and_any_other_oversize_frame_raises():
    line = frame_bytes("error", "tok", {"jobId": "j", "category": "trace", "message": "é" * MAX_FRAME_BYTES})
    assert len(line) <= MAX_FRAME_BYTES
    assert json.loads(line)["category"] == "trace"
    try:
        frame_bytes("phase", "tok", {"phase": "x" * MAX_FRAME_BYTES})
    except ValueError as error:
        assert "phase" in str(error), str(error)
    else:
        raise AssertionError("A frame over the cap with no message to cut must not be written.")


def test_every_command_is_parsed_with_its_fields():
    assert parse_command(b'{"op":"run","jobId":"j","config":"/w/job.json"}\n')["config"] == "/w/job.json"
    assert parse_command(b'{"op":"cancel","jobId":"j"}')["jobId"] == "j"
    assert parse_command(b'{"op":"evict"}\n') == {"op": "evict"}


def test_a_command_the_worker_cannot_follow_is_a_protocol_error():
    bad = [
        b"not json\n",
        b"\n",
        b"\xff\xfe\n",
        b"[1]\n",
        b'{"op":"shutdown"}\n',
        b'{"jobId":"j"}\n',
        b'{"op":"run","jobId":"j"}\n',
        b'{"op":"run","jobId":"","config":"/c"}\n',
        b'{"op":"cancel","jobId":7}\n',
        b'{"op":"evict","pad":"' + b"x" * MAX_FRAME_BYTES + b'"}',
    ]
    for line in bad:
        try:
            parse_command(line)
        except ProtocolError:
            continue
        raise AssertionError(f"{line[:40]!r} must be a protocol error.")


def test_a_cancelled_jobs_outputs_are_removed_and_missing_ones_are_no_error():
    with tempfile.TemporaryDirectory() as directory:
        written = [os.path.join(directory, name) for name in ("trace.jsonl", "result.json")]
        for path in written:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("partial")
        kept = os.path.join(directory, "job.json")
        with open(kept, "w", encoding="utf-8") as handle:
            handle.write("{}")
        remove_outputs(
            {
                **VALID_JOB,
                "tracePath": written[0],
                "resultPath": written[1],
                "attentionPath": os.path.join(directory, "never-written.jsonl"),
            }
        )
        assert os.listdir(directory) == ["job.json"]


def test_job_frames_carry_the_job_id_and_a_phase_boundary_is_a_cancel_point():
    modellab = _load_modellab()
    events = []
    run = modellab._Run("job-7")
    with mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))):
        run.enter("tracing")
        run.cancel.set()
        try:
            run.enter("saving")
        except modellab.JobCancelled:
            pass
        else:
            raise AssertionError("A cancelled job must not enter its next phase.")
    assert events == [("phase", {"jobId": "job-7", "phase": "tracing"})]
    assert run.phase == "tracing"


def _main_with(job, error=None):
    """Runs the script's real main on ``--config`` with every job runner replaced, ``source`` raising ``error`` when
    given. Returns its exit status, its events and the runners that were called."""
    modellab = _load_modellab()
    modellab.torch.__version__ = "2"
    modellab.transformers.__version__ = "5"
    modellab.torch.cuda.OutOfMemoryError = MemoryError
    events = []
    runners = {name: mock.MagicMock() for name in ("trace", "derive", "source")}
    runners["source"].side_effect = error
    with tempfile.TemporaryDirectory() as directory:
        config = os.path.join(directory, "job.json")
        with open(config, "w", encoding="utf-8") as handle:
            json.dump(job, handle)
        with (
            mock.patch.multiple(modellab, **runners),
            mock.patch.object(modellab, "emit", lambda event, **fields: events.append((event, fields))),
            mock.patch.object(sys, "argv", ["modellab.py", "--config", config]),
            mock.patch("os.umask"),
        ):
            status = modellab.main()
    return status, events, [name for name, runner in runners.items() if runner.called]


def _main_failing_with(error, job=None):
    """Runs the script's real main with a source job's runner raising, and returns its error events."""
    status, events, _ = _main_with(job or VALID_SOURCE_JOB, error)
    assert status == 1
    return [fields for event, fields in events if event == "error"]


def test_a_source_job_runs_with_config_and_ends_done():
    status, events, called = _main_with(VALID_SOURCE_JOB)
    assert status == 0
    assert called == ["source"]
    assert [event for event, _ in events][0] == "handshake"
    assert events[-1] == ("done", {"cancelled": False})
    assert not [event for event, _ in events if event == "error"]


def test_a_trace_or_steering_job_is_refused_with_config_and_runs_nothing():
    for job in (VALID_JOB, VALID_STEERING_JOB):
        status, events, called = _main_with(job)
        assert status == 1
        assert called == [], f"A {job['kind']} job must not run outside a serving worker."
        errors = [fields for event, fields in events if event == "error"]
        assert errors == [
            {
                "category": "contract",
                "message": "A trace or steering job runs in a serving worker (--serve), not with --config.",
            }
        ]


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
        "modelKey": [None, "", 1],
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


ATTENTION_FIELDS = {
    "attentionPath": "/work/attention.jsonl",
    "attentionLayers": [3, 7, 11],
    "attentionTopKeys": 32,
    "attentionByteCap": 3_932_320,
}


def test_a_job_with_all_attention_fields_captures_and_one_without_or_with_nulls_does_not():
    assert captures_attention(validate_job({**VALID_JOB, **ATTENTION_FIELDS}))
    assert not captures_attention(validate_job(dict(VALID_JOB)))
    assert not captures_attention(validate_job({**VALID_JOB, **dict.fromkeys(ATTENTION_FIELDS)}))


def test_attention_fields_come_all_four_or_none():
    for missing in ATTENTION_FIELDS:
        for absent in ({}, {missing: None}):
            fields = {**{k: v for k, v in ATTENTION_FIELDS.items() if k != missing}, **absent}
            try:
                validate_job({**VALID_JOB, **fields})
            except JobError as error:
                assert "all four or none" in str(error), str(error)
                continue
            raise AssertionError(f"A job without {missing} but with the other attention fields must be refused.")


def test_every_unusable_attention_field_is_refused():
    bad_values = {
        "attentionPath": ["", 1],
        "attentionLayers": [[], [3, 3], [7, 3], [-1], ["3"], [True], list(range(9)), 3],
        "attentionTopKeys": [0, 65, "32", True, 1.5],
        "attentionByteCap": [FOOTER_RESERVE_BYTES, "9", True],
    }
    for field, values in bad_values.items():
        for value in values:
            try:
                validate_job({**VALID_JOB, **ATTENTION_FIELDS, field: value})
            except JobError as error:
                assert field in str(error), str(error)
                continue
            raise AssertionError(f"{field}={value!r} must be refused.")
    assert validate_job({**VALID_JOB, **ATTENTION_FIELDS, "attentionLayers": list(range(8)), "attentionTopKeys": 64})


LENS_FIELDS = {"lensTopTokens": 5, "lensByteCap": 1_547_424}


def test_a_job_with_both_lens_fields_records_the_lens_and_one_without_or_with_nulls_does_not():
    assert records_lens(validate_job({**VALID_JOB, **LENS_FIELDS}))
    assert not records_lens(validate_job(dict(VALID_JOB)))
    assert not records_lens(validate_job({**VALID_JOB, **dict.fromkeys(LENS_FIELDS)}))


def test_lens_fields_come_both_or_neither():
    for missing in LENS_FIELDS:
        for absent in ({}, {missing: None}):
            fields = {**{k: v for k, v in LENS_FIELDS.items() if k != missing}, **absent}
            try:
                validate_job({**VALID_JOB, **fields})
            except JobError as error:
                assert "both or neither" in str(error), str(error)
                continue
            raise AssertionError(f"A job without {missing} but with the other lens field must be refused.")


def test_every_unusable_lens_field_is_refused():
    bad_values = {
        "lensTopTokens": [0, MAX_LENS_TOP_TOKENS + 1, "5", True, 1.5],
        "lensByteCap": [FOOTER_RESERVE_BYTES, "9", True],
    }
    for field, values in bad_values.items():
        for value in values:
            try:
                validate_job({**VALID_JOB, **LENS_FIELDS, field: value})
            except JobError as error:
                assert field in str(error), str(error)
                continue
            raise AssertionError(f"{field}={value!r} must be refused.")
    assert validate_job({**VALID_JOB, **LENS_FIELDS, "lensTopTokens": 1})
    assert validate_job({**VALID_JOB, **LENS_FIELDS, "lensTopTokens": MAX_LENS_TOP_TOKENS})


def test_a_lens_record_has_the_wire_shape():
    nan, inf = float("nan"), float("inf")
    texts = {7: "a", 9: "b", 3: "c", 11: "unused"}
    record = lens_record(
        4,
        [[7, 9], [3, 7], [9, 3]],
        [[-0.12345, -2.5], [-0.5, -inf], [nan, nan]],
        [-0.12345, -3.0, nan],
        [1, 3, 1],
        [-0.0, 2.0, nan],
        texts,
    )
    assert record == {
        "lens": {
            "s": 4,
            "i": [[7, 9], [3, 7], [9, 3]],
            "p": [[-0.1235, -2.5], [-0.5, None], [None, None]],
            "c": [-0.1235, -3.0, None],
            # A NaN row: no logprob, no rank (it would read 1) and no entropy.
            "r": [1, 3, None],
            "e": [0.0, 2.0, None],
            "t": {"3": "c", "7": "a", "9": "b"},
        }
    }
    # A one-hot readout's -0.0 is written as 0.0, never "-0.0".
    assert '"e":[0.0,2.0,null]' in json.dumps(record, separators=(",", ":"))
    assert lens_record(0, [[1]], [[-1.0]], [-1.0], [1], [1.23456], {1: "x"})["lens"]["e"] == [1.235]
    # A masked chosen token (-inf) has no rank either: r is null exactly where c is.
    assert lens_record(0, [[1]], [[-1.0]], [-inf], [7], [1.0], {1: "x"})["lens"]["r"] == [None]


def test_the_chosen_rank_shares_the_better_rank_on_a_tie():
    class Row:
        # The two tensor operations competition_rank uses, on a plain list.
        def __init__(self, items):
            self.items = list(items)

        def __gt__(self, value):
            return Row(item > value for item in self.items)

        def sum(self, _dim):
            return sum(self.items)

    row = Row([-0.5, -1.0, -0.5, -2.0, -1.0])
    assert competition_rank(row, -0.5) == 1
    assert competition_rank(row, -1.0) == 3
    assert competition_rank(row, -2.0) == 5
    # A NaN chosen value compares above nothing: the helper says 1, which is why lens_record nulls it.
    nan = float("nan")
    assert competition_rank(Row([nan, nan, nan]), nan) == 1
    assert lens_record(0, [[0]], [[nan]], [nan], [competition_rank(Row([nan]), nan)], [nan], {0: ""})["lens"]["r"] == [
        None
    ]


def test_the_lens_is_read_only_where_the_batched_rows_are_the_models_own():
    from types import SimpleNamespace

    plain = SimpleNamespace(hidden_size=1024)
    neutral = SimpleNamespace(final_logit_softcapping=None, logits_scaling=1.0, logit_scale=1)
    assert lens_readable(24, object(), object(), plain)
    assert lens_readable(2, object(), object(), neutral)
    assert not lens_readable(1, object(), object(), plain)
    assert not lens_readable(0, object(), object(), plain)
    assert not lens_readable(24, None, object(), plain)
    assert not lens_readable(24, object(), None, plain)
    for name, value in (("final_logit_softcapping", 30.0), ("logits_scaling", 8.0), ("logit_scale", 0.0625)):
        assert not lens_readable(24, object(), object(), SimpleNamespace(**{name: value})), name


def _written(text):
    return len(json.dumps(text)) - 2


def test_a_lens_text_is_cut_by_its_written_escaped_bytes():
    for text in ("\u4e2d" * 20, "\U0001f600" * 10, "\x01" * 20, "a\u4e2d\U0001f600\x01" * 8, '"\\' * 40):
        cut = cut_written_text(text)
        assert _written(cut) <= CANDIDATE_TEXT_BYTES, cut
        assert cut.endswith(CUT_MARK)
        assert text.startswith(cut[: -len(CUT_MARK)])
    # 45 UTF-8 bytes but 90 written: cut_text keeps it whole, the lens cut does not.
    assert cut_text("\u4e2d" * 15) == "\u4e2d" * 15
    assert cut_written_text("\u4e2d" * 15) == "\u4e2d" * 9 + CUT_MARK
    assert cut_written_text("x" * CANDIDATE_TEXT_BYTES) == "x" * CANDIDATE_TEXT_BYTES


def _line_bytes(record):
    return len((json.dumps(record, separators=(",", ":"), allow_nan=False) + "\n").encode())


def test_the_worst_lens_line_fits_its_share_of_the_cap():
    blocks, top = 64, 5
    # ModelLabTraceSpec.LensByteCapFor(64): 128 lines and the footer reserve.
    cap = 128 * (64 + blocks * (121 + top * 76)) + FOOTER_RESERVE_BYTES
    ids = [[248_319 - (block * top + slot) for slot in range(top)] for block in range(blocks)]
    worst = ("\u4e2d" * 40, "\U0001f600" * 40, "\x01" * 40, "x" * 200, '"' * 100)
    texts = {token: worst[token % len(worst)] for row in ids for token in row}
    record = lens_record(
        127, ids, [[-1234.5678] * top] * blocks, [-1234.5678] * blocks, [248_320] * blocks, [12.422] * blocks, texts
    )
    assert len(record["lens"]["t"]) == blocks * top
    assert all(_written(text) <= CANDIDATE_TEXT_BYTES for text in record["lens"]["t"].values())
    assert _line_bytes(record) <= (cap - FOOTER_RESERVE_BYTES) // 128, _line_bytes(record)


def _hook_and_lens(hook_cap, hook_count, lens_cap, lens_count, with_lens=True):
    lens_line = lens_record(0, [[1, 2]], [[-0.1, -2.0]], [-0.1], [1], [0.5], {1: "a", 2: "b"})
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, "trace.jsonl")
        writer = CappedTraceWriter(path, hook_cap)
        for _ in range(hook_count):
            writer.write(RECORD)
        if with_lens:
            section = writer.section(lens_cap, "lensFooter")
            for _ in range(lens_count):
                section.write(lens_line)
            section.close()
        writer.close()
        with open(path, "rb") as handle:
            raw = handle.read()
        return raw, _lines(path)


def _parts(lines):
    hook = [line for line in lines if "footer" not in line and "lens" not in line and "lensFooter" not in line]
    lens = [line for line in lines if "lens" in line]
    (footer,) = [line for line in lines if "footer" in line]
    (lens_footer,) = [line["lensFooter"] for line in lines if "lensFooter" in line]
    return hook, footer, lens, lens_footer


def test_a_lens_overflow_leaves_the_hook_records_and_footer_byte_for_byte():
    alone, _ = _hook_and_lens(100_000, 10, 0, 0, with_lens=False)
    raw, lines = _hook_and_lens(100_000, 10, 1_000, 500)
    assert len(raw) <= 100_000 + 1_000
    hook, footer, lens, lens_footer = _parts(lines)
    assert len(hook) == 10
    assert footer == json.loads(alone.splitlines()[-1])
    # The hook part is today's bytes; the lens part only adds lines.
    hook_bytes = [line for line in raw.splitlines() if not line.startswith((b'{"lens"', b'{"lensFooter"'))]
    assert hook_bytes == alone.splitlines()
    assert lens_footer["truncated"] is True
    assert lens_footer["records"] == len(lens)
    assert lens_footer["records"] + lens_footer["dropped"] == 500
    assert lens_footer["cap"] == 1_000
    assert lens_footer["payloadBytes"] <= 1_000 - FOOTER_RESERVE_BYTES


def test_a_hook_overflow_leaves_the_lens_section_writing():
    raw, lines = _hook_and_lens(1_000, 500, 100_000, 20)
    assert len(raw) <= 1_000 + 100_000
    hook, footer, lens, lens_footer = _parts(lines)
    assert footer["truncated"] is True
    assert footer["records"] == len(hook)
    assert len(lens) == 20
    assert lens_footer == {
        "truncated": False,
        "records": 20,
        "dropped": 0,
        "payloadBytes": 20 * _line_bytes(lens[0]),
        "cap": 100_000,
    }


class _Hidden:
    """A block output's shape and its last position, enough for the lens hook."""

    def __init__(self, tokens, tag, width=4, batch=1):
        self.shape = (batch, tokens, width)
        self._tag = tag

    def dim(self):
        return len(self.shape)

    def __getitem__(self, index):
        assert index == (0, -1), index
        return mock.Mock(clone=mock.Mock(return_value=self._tag))


def _lens_with_hooks(blocks=3, width=4):
    modellab = _load_modellab()
    model = mock.MagicMock()
    model.config.get_text_config.return_value.hidden_size = width
    block_modules = [mock.MagicMock() for _ in range(blocks)]
    lens = modellab._Lens(contextlib.ExitStack(), model, block_modules, 5, mock.MagicMock(), mock.MagicMock())
    hooks = [module.register_forward_hook.call_args.args[0] for module in block_modules[:-1]]
    # The last block has no hook: its readout is the model's own logits.
    assert not block_modules[-1].register_forward_hook.called
    return lens, hooks


def test_the_lens_hook_keeps_the_last_position_of_a_tuple_or_bare_output_at_prefill_and_decode():
    lens, hooks = _lens_with_hooks()
    hooks[0](None, None, (_Hidden(17, "prefill-0"), None))
    hooks[1](None, None, _Hidden(17, "prefill-1"))
    assert lens._stash == {0: "prefill-0", 1: "prefill-1"}
    hooks[0](None, None, _Hidden(1, "decode-0"))
    hooks[1](None, None, (_Hidden(1, "decode-1"),))
    assert lens._stash == {0: "decode-0", 1: "decode-1"}


def test_the_lens_hook_refuses_an_output_of_another_shape():
    lens, hooks = _lens_with_hooks(width=4)
    for wrong in (_Hidden(3, "x", width=5), _Hidden(3, "x", batch=2)):
        try:
            hooks[0](None, None, wrong)
        except RuntimeError as error:
            assert "Block 0" in str(error), str(error)
            continue
        raise AssertionError(f"{wrong.shape} must be refused.")
    assert lens._stash == {}


def test_a_chunked_prefill_keeps_the_last_chunk_and_a_missing_block_fails_the_step():
    lens, hooks = _lens_with_hooks()
    for chunk in ("chunk-1", "chunk-2"):
        for index, hook in enumerate(hooks):
            hook(None, None, _Hidden(8, f"{chunk}-{index}"))
    assert lens._stash == {0: "chunk-2-0", 1: "chunk-2-1"}

    lens, hooks = _lens_with_hooks()
    hooks[0](None, None, _Hidden(1, "only-0"))
    lens._read_raw(None, None, mock.MagicMock())
    try:
        lens(None, mock.MagicMock())
    except RuntimeError as error:
        assert "blocks [1]" in str(error), str(error)
    else:
        raise AssertionError("A step without every block's state must fail.")

    lens, hooks = _lens_with_hooks()
    for index, hook in enumerate(hooks):
        hook(None, None, _Hidden(1, index))
    try:
        lens(None, mock.MagicMock())
    except RuntimeError as error:
        assert "logits" in str(error), str(error)
    else:
        raise AssertionError("A step without the model's logits must fail.")


def test_an_attention_record_has_the_wire_shape():
    record = attention_record(2, 7, 14, [[14, 0], [3, 14]], [[0.6, 0.25], [0.5, 0.123456]])
    assert record == {
        "s": 2,
        "l": 7,
        "q": 14,
        "k": [[14, 0], [3, 14]],
        "w": [[6000, 2500], [5000, 1234]],
        "r": [1500, 3765],
    }
    assert json.loads(json.dumps(record, allow_nan=False)) == record
    assert ATTENTION_WEIGHT_SCALE == 10_000


def test_an_attention_row_rounded_down_never_sums_past_the_scale():
    rows = [
        [0.33333334, 0.33333334, 0.33333334],  # a float32 softmax row a hair past 1
        [0.99995, 0.00004, 0.00001],
        [0.12345678] * 8,
        [1.0],
    ]
    record = attention_record(0, 3, 0, [list(range(len(row))) for row in rows], rows)
    for weights, rest in zip(record["w"], record["r"], strict=True):
        assert rest >= 0
        assert sum(weights) + rest <= ATTENTION_WEIGHT_SCALE, (weights, rest)
    assert record["r"][0] == 0  # clamped, not negative
    assert record["w"][3] == [10_000]


def test_a_non_finite_attention_weight_is_a_clear_job_error():
    for bad in (float("nan"), float("inf")):
        try:
            attention_record(0, 3, 1, [[1, 0]], [[bad, 0.25]])
        except JobError as error:
            assert "attention weight was not a finite number" in str(error)
            continue
        raise AssertionError(f"A {bad} weight must not reach the attention file.")


def test_a_non_finite_result_value_is_refused_rather_than_written_as_invalid_json():
    result = _trace_result("x")
    result["seconds"] = float("nan")
    try:
        fitted_trace_result(result)
    except ValueError:
        pass
    else:
        raise AssertionError("A trace result must stay strict JSON.")
    modellab = _load_modellab()
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, "result.json")
        try:
            modellab.write_result({"resultPath": path}, {"seconds": float("inf")})
        except ValueError:
            return
    raise AssertionError("A steering result must stay strict JSON.")


def test_a_query_with_fewer_keys_than_asked_for_keeps_a_short_row():
    # The first predicting query of a two-token prompt sees two keys, whatever attentionTopKeys asks for.
    record = attention_record(0, 3, 1, [[1, 0]] * 2, [[0.75, 0.25]] * 2)
    assert record["k"] == [[1, 0], [1, 0]]
    assert record["w"] == [[7500, 2500], [7500, 2500]]
    assert record["r"] == [0, 0]


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
    "modelKey": "artifact-1:rev-2:bf16:6",
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
        "modelKey": [None, ""],
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


def test_the_contract_is_version_6_and_a_version_5_job_is_refused():
    assert CONTRACT_VERSION == 6
    for job in (VALID_JOB, VALID_STEERING_JOB, VALID_SOURCE_JOB):
        try:
            validate_job({**job, "contractVersion": 5})
        except JobError:
            continue
        raise AssertionError(f"A version 5 {job['kind']} job must be refused.")


def test_a_source_job_carries_no_model_key():
    try:
        validate_job({**VALID_SOURCE_JOB, "modelKey": "k"})
    except JobError as error:
        assert "modelKey" in str(error), str(error)
    else:
        raise AssertionError("A source job loads no model, so a modelKey is a field it may not carry.")


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
    errors = _main_failing_with(SourceRefusalError("remote_code", "config.json names repository code."))
    assert errors == [{"category": "remote_code", "message": "config.json names repository code."}]


if __name__ == "__main__":
    for name, test in sorted(globals().items()):
        if name.startswith("test_") and callable(test):
            test()
            print(f"ok {name}")
