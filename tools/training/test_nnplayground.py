"""Checks for nnplayground.py on real CPU torch. Run: ``python3 -m pytest tools/training/test_nnplayground.py``.

Torch is the root dev venv's CPU wheel. Off Linux x86_64 (where that venv carries no torch) every torch test skips;
on Linux x86_64 a missing torch fails them.
"""

import contextlib
import copy
import json
import math
import os
import platform
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from typing import Any

import nnplayground
from modellablib import JobError
from trainlib import CANCELLED_EXIT_CODE

try:
    import torch
except ImportError:
    torch = None

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "nnplayground.py")
RUN_ID = "run-id-for-tests"
RTOL, ATOL = 1e-10, 1e-12


def _torch():
    if torch is None:
        if sys.platform == "linux" and platform.machine() == "x86_64":
            raise AssertionError("torch is missing: the root dev venv pins CPU torch on Linux x86_64.")
        raise unittest.SkipTest("torch is not installed: the root dev venv carries CPU torch on Linux x86_64 only.")
    return torch


def _config(**overrides):
    config = {
        "dataset": "xor",
        "learningRate": 0.5,
        "residual": False,
        "sharedBlock": False,
        "depth": 2,
        "hidden": 8,
        "steps": 16,
        "seed": 0,
    }
    return {**config, **overrides}


def _train(after_backward=None, **overrides) -> tuple[dict[str, Any], dict[str, Any]]:
    config = _config(**overrides)
    x, y = nnplayground.dataset(config["dataset"])
    return nnplayground.train(config, x, y, after_backward)


def _job(directory, **overrides) -> dict[str, Any]:
    return {
        "contractVersion": 1,
        "kind": "playground",
        "template": "tiny-mlp-v1",
        "config": _config(**overrides),
        "tracePath": os.path.join(directory, "trace.json"),
        "resultPath": os.path.join(directory, "result.json"),
        "maxTraceBytes": nnplayground.MAX_TRACE_BYTES,
        "maxResultBytes": nnplayground.MAX_RESULT_BYTES,
    }


def _close(actual, expected):
    """A recorded number (or nested list) against the reference tensor."""
    torch = _torch()
    torch.testing.assert_close(torch.tensor(actual, dtype=torch.float64), expected.detach(), rtol=RTOL, atol=ATOL)


# The independent reference: a functional network from the trace's step-0 parameters, one leaf per visit.


def _reference(config, x, y, recorded_before):
    """Per step: what the trace must hold. A shared block gets separate equal-valued leaves per visit; their gradients
    are summed into ONE update of the shared parameter, and the next step's leaves are made from it.

    The reference evolves on its own between recorded steps, and its evolved parameters are the expected ``before``.
    At a recorded step it then continues from the recorded ``before``: float64 reassociation drift grows over hundreds
    of steps past the tolerance, so it may only build up over one gap."""
    torch = _torch()
    inputs = torch.tensor(x, dtype=torch.float64)
    targets = torch.tensor(y, dtype=torch.float64)
    rows = nnplayground.probes(len(x))
    params = {name: torch.tensor(value, dtype=torch.float64) for name, value in recorded_before[0].items()}
    depth, rate, shared = config["depth"], config["learningRate"], config["sharedBlock"]

    def leaf(name):
        return params[name].clone().requires_grad_()

    steps = {}
    for step in range(config["steps"]):
        evolved = params
        if step in recorded_before:
            params = {name: torch.tensor(value, dtype=torch.float64) for name, value in recorded_before[step].items()}
        embed = (leaf("embed.weight"), leaf("embed.bias"))
        if shared:
            visits = [(leaf("block.weight"), leaf("block.bias")) for _ in range(depth)]
        else:
            visits = [(leaf(f"blocks.{i}.weight"), leaf(f"blocks.{i}.bias")) for i in range(depth)]
        head = (leaf("head.weight"), leaf("head.bias"))
        observed_inputs, outputs, streams = [inputs], [], []
        h = torch.tanh(inputs @ embed[0].T + embed[1])
        outputs.append(h)
        for weight, bias in visits:
            observed_inputs.append(h)
            output = torch.tanh(h @ weight.T + bias)
            outputs.append(output)
            h = h + output if config["residual"] else output
            streams.append(h)
        observed_inputs.append(h)
        z = h @ head[0].T + head[1]
        outputs.append(z)
        logit = z[:, 0]
        # Mean BCE with logits written out: max(z, 0) - z·y + log(1 + e^-|z|).
        loss = (logit.clamp(min=0) - logit * targets + torch.log1p(torch.exp(-logit.abs()))).mean()
        visit_leaves = [tensor for pair in visits for tensor in pair]
        leaves = [*embed, *visit_leaves, *head]
        grads = torch.autograd.grad(loss, leaves + outputs)
        leaf_grads, output_grads = grads[: len(leaves)], grads[len(leaves) :]
        visit_grads = [(leaf_grads[2 + 2 * k], leaf_grads[3 + 2 * k]) for k in range(depth)]
        named = {"embed.weight": leaf_grads[0], "embed.bias": leaf_grads[1]}
        if shared:
            named["block.weight"] = sum(pair[0] for pair in visit_grads)
            named["block.bias"] = sum(pair[1] for pair in visit_grads)
        else:
            for k, (weight_grad, bias_grad) in enumerate(visit_grads):
                named[f"blocks.{k}.weight"], named[f"blocks.{k}.bias"] = weight_grad, bias_grad
        named["head.weight"], named["head.bias"] = leaf_grads[-2], leaf_grads[-1]
        after = {name: params[name] - rate * named[name] for name in params}
        steps[step] = {
            "loss": loss,
            "probeLogits": logit[rows],
            "inputs": [value[rows] for value in observed_inputs],
            "outputs": [value[rows] for value in outputs],
            "streams": [value[rows] for value in streams],
            "outputGrads": [value[rows] for value in output_grads],
            "parameterGrads": named,
            "visitGrads": visit_grads if shared else [],
            "before": evolved,
            "after": after,
        }
        params = after
    return steps


def _compare(record, expected, trace):
    names = [parameter["id"] for parameter in trace["parameters"]]
    config = trace["config"]
    _close(record["loss"]["value"], expected["loss"])
    _close(record["loss"]["probeLogits"], expected["probeLogits"])
    for o, observed in enumerate(record["forward"]):
        assert observed["o"] == o
        _close(observed["input"], expected["inputs"][o])
        _close(observed["output"], expected["outputs"][o])
        if config["residual"] and 0 < o <= config["depth"]:
            _close(observed["stream"], expected["streams"][o - 1])
        else:
            assert "stream" not in observed
    backward = record["backward"]
    for o, entry in enumerate(backward["outputGrads"]):
        assert entry["o"] == o
        _close(entry["grad"], expected["outputGrads"][o])
    for p, entry in enumerate(backward["parameterGrads"]):
        assert entry["p"] == p
        _close(entry["grad"], expected["parameterGrads"][names[p]])
    assert len(backward["visitGrads"]) == 2 * len(expected["visitGrads"])
    for entry in backward["visitGrads"]:
        which = 0 if names[entry["p"]] == "block.weight" else 1
        _close(entry["grad"], expected["visitGrads"][entry["o"] - 1][which])
    for p, entry in enumerate(record["update"]):
        assert entry["p"] == p
        _close(entry["before"], expected["before"][names[p]])
        _close(entry["after"], expected["after"][names[p]])


def _check_against_reference(trace):
    config = trace["config"]
    x, y = nnplayground.dataset(config["dataset"])
    names = [parameter["id"] for parameter in trace["parameters"]]
    recorded_before = {
        record["step"]: {name: entry["before"] for name, entry in zip(names, record["update"], strict=True)}
        for record in trace["recorded"]
    }
    expected = _reference(config, x, y, recorded_before)
    assert [record["step"] for record in trace["recorded"]] == nnplayground.recorded_steps(config["steps"])
    for record in trace["recorded"]:
        _compare(record, expected[record["step"]], trace)
    return expected


def test_train_matches_the_independent_reference_in_every_variant():
    _torch()
    for dataset in nnplayground.DATASETS:
        for residual in (False, True):
            for shared in (False, True):
                for steps in (1, 16, 17, 200):
                    trace, _ = _train(
                        dataset=dataset, residual=residual, sharedBlock=shared, steps=steps, depth=3, hidden=5
                    )
                    _check_against_reference(trace)


def test_swapped_visit_gradients_fail_the_reference():
    _torch()
    trace, _ = _train(sharedBlock=True, depth=3, hidden=5, steps=4)
    expected = _check_against_reference(trace)
    swapped = copy.copy(expected[2])
    visits = list(swapped["visitGrads"])
    visits[0], visits[1] = visits[1], visits[0]
    swapped["visitGrads"] = visits
    try:
        _compare(trace["recorded"][2], swapped, trace)
    except AssertionError:
        return
    raise AssertionError("Swapping two visits' gradients passed the reference check.")


def test_every_written_number_parses_back_exactly():
    _torch()
    trace, result = _train(dataset="spirals", sharedBlock=True, residual=True, depth=3, steps=17)
    for value in (trace, result):
        text = nnplayground.serialized(value, nnplayground.MAX_TRACE_BYTES, "trace").decode()
        assert json.loads(text) == value
        assert "NaN" not in text
        assert "Infinity" not in text


# Structure: modules, parameters and occurrences.


def test_a_shared_block_has_one_parameter_set_and_one_observation_per_visit():
    _torch()
    trace, _ = _train(sharedBlock=True, depth=3)
    assert [module["path"] for module in trace["modules"]] == ["embed", "block", "head"]
    assert [module["outputActivation"] for module in trace["modules"]] == ["tanh", "tanh", None]
    ids = [parameter["id"] for parameter in trace["parameters"]]
    assert ids == ["embed.weight", "embed.bias", "block.weight", "block.bias", "head.weight", "head.bias"]
    assert [parameter["module"] for parameter in trace["parameters"]] == [
        "embed",
        "embed",
        "block",
        "block",
        "head",
        "head",
    ]
    assert trace["occurrences"] == [
        {"module": "embed", "visit": 0},
        {"module": "block", "visit": 0},
        {"module": "block", "visit": 1},
        {"module": "block", "visit": 2},
        {"module": "head", "visit": 0},
    ]
    record = trace["recorded"][0]
    assert len(record["forward"]) == 5
    visit_grads = record["backward"]["visitGrads"]
    for p in (2, 3):
        assert sorted(entry["o"] for entry in visit_grads if entry["p"] == p) == [1, 2, 3]
    assert len(visit_grads) == 6


def test_independent_blocks_have_their_own_parameters_and_no_visit_gradients():
    _torch()
    trace, _ = _train(depth=3)
    assert [module["path"] for module in trace["modules"]] == ["embed", "blocks.0", "blocks.1", "blocks.2", "head"]
    assert [module["outputActivation"] for module in trace["modules"]] == ["tanh"] * 4 + [None]
    assert len(trace["parameters"]) == 10
    assert [occurrence["visit"] for occurrence in trace["occurrences"]] == [0] * 5
    assert all(record["backward"]["visitGrads"] == [] for record in trace["recorded"])


def test_the_residual_stream_is_input_plus_output():
    _torch()
    trace, _ = _train(residual=True, depth=3)
    for record in trace["recorded"]:
        for observed in record["forward"][1:4]:
            sums = [
                [a + b for a, b in zip(i, o, strict=True)]
                for i, o in zip(observed["input"], observed["output"], strict=True)
            ]
            assert observed["stream"] == sums


def test_the_trace_header_and_the_result():
    _torch()
    trace, result = _train(steps=20)
    assert trace["formatVersion"] == 1
    assert trace["template"] == "tiny-mlp-v1"
    assert (trace["device"], trace["dtype"], trace["threads"]) == ("cpu", "float64", 1)
    assert trace["stepDefinition"]["optimizer"] == "SGD"
    assert trace["data"] == {
        "count": 4,
        "probes": [0, 1, 2, 3],
        "x": nnplayground.dataset("xor")[0],
        "y": [0.0, 1.0, 1.0, 0.0],
    }
    assert trace["outcome"] == {"kind": "finished", "atStep": None, "updatesApplied": 20}
    assert set(result) == {"formatVersion", "template", "config", "outcome", "finalLoss", "finalAccuracy"}
    assert result["outcome"] == trace["outcome"]
    assert math.isfinite(result["finalLoss"])


# Divergence: (a) a non-finite loss, (b) a non-finite gradient, at step 0 and after updates.


def test_a_non_finite_loss_diverges_with_no_backward_and_no_update():
    _torch()
    config = _config(steps=5)
    x, y = nnplayground.dataset("xor")
    x = [[math.nan, -1.0], *x[1:]]
    trace, result = nnplayground.train(config, x, y)
    assert trace["outcome"] == {"kind": "diverged", "atStep": 0, "updatesApplied": 0}
    assert len(trace["recorded"]) == 1
    record = trace["recorded"][0]
    assert (record["step"], record["backward"], record["update"]) == (0, None, None)
    assert record["loss"]["value"] is None
    assert result["finalLoss"] is None
    assert result["finalAccuracy"] is None
    assert trace["data"]["x"][0] == [None, -1.0]
    json.dumps(trace, allow_nan=False)


def _poison_at(poisoned_step):
    def after_backward(model, step):
        if step == poisoned_step:
            model.head.weight.grad[0, 0] = math.inf

    return after_backward


def test_a_non_finite_gradient_diverges_with_a_backward_and_no_update():
    _torch()
    trace, result = _train(after_backward=_poison_at(0), steps=5)
    assert trace["outcome"] == {"kind": "diverged", "atStep": 0, "updatesApplied": 0}
    record = trace["recorded"][0]
    assert record["update"] is None
    head = [parameter["id"] for parameter in trace["parameters"]].index("head.weight")
    assert record["backward"]["parameterGrads"][head]["grad"][0][0] is None
    assert math.isfinite(record["loss"]["value"])
    assert result["finalLoss"] == record["loss"]["value"]
    json.dumps(trace, allow_nan=False)


def test_a_divergence_after_updates_keeps_the_updates_before_it():
    _torch()
    trace, result = _train(after_backward=_poison_at(3), steps=10)
    assert trace["outcome"] == {"kind": "diverged", "atStep": 3, "updatesApplied": 3}
    assert result["outcome"] == trace["outcome"]
    assert [record["step"] for record in trace["recorded"]] == [0, 1, 2, 3]
    assert all(record["update"] is not None for record in trace["recorded"][:3])
    assert trace["recorded"][3]["update"] is None
    # Between chosen steps of a long run: the chosen ones before it, then it.
    long, _ = _train(after_backward=_poison_at(500), steps=2000)
    assert [record["step"] for record in long["recorded"]] == nnplayground.recorded_steps(2000, 500)


# Recorded steps.


def test_recorded_steps():
    assert nnplayground.recorded_steps(1) == [0]
    assert nnplayground.recorded_steps(16) == list(range(16))
    for steps in (17, 18, 100, 2000):
        chosen = nnplayground.recorded_steps(steps)
        assert len(set(chosen)) == 16
        assert chosen == sorted(chosen)
        assert (chosen[0], chosen[-1]) == (0, steps - 1)
    chosen = nnplayground.recorded_steps(2000)
    assert nnplayground.recorded_steps(2000, 0) == [0]
    assert nnplayground.recorded_steps(2000, chosen[3]) == chosen[:4]
    assert nnplayground.recorded_steps(2000, chosen[3] + 1) == [*chosen[:4], chosen[3] + 1]
    assert nnplayground.recorded_steps(2000, 1998) == [*chosen[:15], 1998]
    assert nnplayground.recorded_steps(10, 3) == [0, 1, 2, 3]


# Bounds and the job contract.


def _refused(job, directory):
    try:
        nnplayground.validate_job(job, directory)
    except JobError:
        return True
    return False


def test_validate_job_accepts_every_limit():
    with tempfile.TemporaryDirectory() as directory:
        for name, (low, high) in nnplayground.KNOB_BOUNDS.items():
            for value in (low, high):
                job = _job(directory, **{name: value})
                assert nnplayground.validate_job(job, directory)["config"][name] == value
        assert nnplayground.validate_job(_job(directory, learningRate=1), directory)["config"]["learningRate"] == 1.0
        assert (
            nnplayground.validate_job(_job(directory, dataset="spirals"), directory)["config"]["dataset"] == "spirals"
        )


def test_validate_job_refuses_every_wrong_field():
    with tempfile.TemporaryDirectory() as directory:
        job = _job(directory)
        cases: list[Any] = []
        for name in job:
            cases.append({key: value for key, value in job.items() if key != name})
            for bad in (None, "1", True, 1.5, [], {}):
                if not (name in ("tracePath", "resultPath") and bad == "1"):
                    cases.append({**job, name: bad})
        cases.append({**job, "extra": 1})
        cases.append({**job, "config": {**job["config"], "extra": 1}})
        for name in job["config"]:
            cases.append({**job, "config": {key: value for key, value in job["config"].items() if key != name}})
            for bad in (None, "1", [], {}):
                cases.append({**job, "config": {**job["config"], name: bad}})
        for name, (low, high) in nnplayground.KNOB_BOUNDS.items():
            step = 0.00001 if name == "learningRate" else 1
            for bad in (low - step, high + step):
                cases.append({**job, "config": {**job["config"], name: bad}})
        for name in ("depth", "hidden", "steps", "seed"):
            cases.extend({**job, "config": {**job["config"], name: bad}} for bad in (True, 2.0))
        for bad in (True, math.nan, math.inf):
            cases.append({**job, "config": {**job["config"], "learningRate": bad}})
        for name in ("residual", "sharedBlock"):
            cases.append({**job, "config": {**job["config"], name: 1}})
        cases.append({**job, "config": {**job["config"], "dataset": "moons"}})
        cases.append({**job, "contractVersion": 2})
        cases.append({**job, "contractVersion": True})
        cases.append({**job, "kind": "trace"})
        cases.append({**job, "template": "tiny-mlp-v2"})
        cases.append({**job, "tracePath": "/elsewhere/trace.json"})
        cases.append({**job, "resultPath": os.path.join(directory, "sub", "result.json")})
        cases.append({**job, "resultPath": job["tracePath"]})
        cases.append({**job, "maxTraceBytes": nnplayground.MAX_TRACE_BYTES + 1})
        cases.append({**job, "maxResultBytes": 0})
        cases.append([])
        for case in cases:
            assert _refused(case, directory), case


def test_the_largest_configs_fit_the_caps_with_margin():
    _torch()
    for shared in (False, True):
        trace, result = _train(dataset="spirals", residual=True, sharedBlock=shared, depth=4, hidden=16, steps=2000)
        assert (
            len(nnplayground.serialized(trace, nnplayground.MAX_TRACE_BYTES, "trace"))
            < nnplayground.MAX_TRACE_BYTES * 0.75
        )
        assert (
            len(nnplayground.serialized(result, nnplayground.MAX_RESULT_BYTES, "result"))
            < nnplayground.MAX_RESULT_BYTES / 4
        )


def test_the_default_xor_config_fits():
    _torch()
    for seed in range(3):
        _, result = _train(steps=500, seed=seed)
        assert result["finalAccuracy"] == 1.0


def test_train_is_deterministic_in_one_environment():
    _torch()
    assert _train(sharedBlock=True, residual=True, steps=40) == _train(sharedBlock=True, residual=True, steps=40)


# The script as the host runs it.


def _spawn(directory, job):
    config_path = os.path.join(directory, "job.json")
    with open(config_path, "w", encoding="utf-8") as handle:
        json.dump(job, handle)
    return subprocess.Popen(  # noqa: S603 - this interpreter running the script under test
        [sys.executable, SCRIPT, "--config", config_path],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        env={**os.environ, "XE_TRAINING_RUN_TOKEN": RUN_ID},
    )


def _run(directory, job):
    process = _spawn(directory, job)
    stdout, _ = process.communicate(timeout=120)
    return process.returncode, [json.loads(line) for line in stdout.splitlines()]


def test_the_script_writes_frames_in_order_and_identical_outputs():
    _torch()
    outputs = []
    for _ in range(2):
        with tempfile.TemporaryDirectory() as directory:
            job = _job(directory, sharedBlock=True, residual=True, steps=40)
            code, frames = _run(directory, job)
            assert code == 0, frames
            assert all(frame["token"] == RUN_ID for frame in frames)
            assert [frame["event"] for frame in frames] == ["handshake", "phase", "phase", "phase", "done"]
            assert frames[0]["contractVersion"] == 1
            assert frames[0]["torch"] == _torch().__version__
            assert [frame["phase"] for frame in frames[1:4]] == ["loading", "training", "saving"]
            with open(job["tracePath"], "rb") as trace, open(job["resultPath"], "rb") as result:
                outputs.append((trace.read(), result.read()))
    assert outputs[0] == outputs[1]
    assert json.loads(outputs[0][1])["outcome"]["kind"] == "finished"


def test_the_script_reports_contract_and_size_errors():
    _torch()
    with tempfile.TemporaryDirectory() as directory:
        code, frames = _run(directory, {**_job(directory), "contractVersion": 8})
        assert code == 1
        assert frames[-1]["event"] == "error"
        assert frames[-1]["category"] == "contract"
        assert frames[-1]["message"].startswith("JobError: ")
        code, frames = _run(directory, {**_job(directory), "maxTraceBytes": 1})
        assert code == 1
        assert frames[-1]["category"] == "result_too_large"
        assert not os.path.exists(os.path.join(directory, "trace.json"))


def test_sigterm_exits_cancelled_and_writes_nothing():
    _torch()
    with tempfile.TemporaryDirectory() as directory:
        job = _job(directory, dataset="spirals", residual=True, depth=4, hidden=16, steps=2000)
        process = _spawn(directory, job)
        assert process.stdout is not None
        training = threading.Event()

        def watch(stdout):
            for line in stdout:
                if json.loads(line).get("phase") == "training":
                    training.set()
                    return

        with contextlib.closing(process.stdout) as stdout:
            reader = threading.Thread(target=watch, args=(stdout,), daemon=True)
            reader.start()
            try:
                assert training.wait(timeout=60), "the script never reported the training phase"
                process.send_signal(signal.SIGTERM)
                assert process.wait(timeout=60) == CANCELLED_EXIT_CODE
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait()
                reader.join(timeout=10)
        assert not os.path.exists(job["tracePath"])
        assert not os.path.exists(job["resultPath"])
