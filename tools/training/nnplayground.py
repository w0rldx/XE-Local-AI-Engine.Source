"""Model Lab training stepper: trains one engine-authored tiny network on the CPU and records chosen steps.

``python nnplayground.py --config job.json`` runs one ``ModelLabPlaygroundJobV1`` (the host's camelCase job) and
exits: 0 after ``done``, 1 after ``error``, 3 (``CANCELLED_EXIT_CODE``) on SIGTERM, writing nothing then. Frames on
stdout, each one JSON line carrying ``token`` (the spawn's ``XE_TRAINING_RUN_TOKEN``):

    {"event": "handshake", "contractVersion": 1, "torch"}
    {"event": "phase",     "phase": "loading" | "training" | "saving"}
    {"event": "done"}
    {"event": "error",     "category": "contract" | "result_too_large" | "internal", "message"}

The template ``tiny-mlp-v1``: embed = tanh(Linear(2 -> H)); ``depth`` block visits tanh(F.linear(h, w, b)), one
shared module ``block`` or independent ``blocks.i``, residual ``h + Block(h)`` or plain ``Block(h)``; head =
Linear(H -> 1), a logit. A step is a full-batch forward, mean BCE-with-logits, ``zero_grad(set_to_none=True)``,
backward, then plain SGD. CUDA is hidden before torch loads; one thread, deterministic algorithms, float64. Torch is
imported only inside ``main`` and ``train``, so the constants and ``validate_job`` load without it.
"""

import argparse
import json
import math
import os
import signal
import sys
from typing import Any

from modellablib import JobError, is_int, write_frame
from trainlib import CANCELLED_EXIT_CODE

# Separate from modellablib.CONTRACT_VERSION: the tracer and this script change independently.
CONTRACT_VERSION = 1
FORMAT_VERSION = 1
KIND = "playground"
TEMPLATE = "tiny-mlp-v1"

# The host's ModelLabPlaygroundSpec holds the same values; scripts/tests compare them with openapi/v1.json.
DATASETS = ("xor", "spirals")
KNOB_BOUNDS = {
    "learningRate": (0.0001, 10.0),
    "depth": (1, 4),
    "hidden": (2, 16),
    "steps": (1, 2000),
    "seed": (0, 2147483647),
}
MAX_TRACE_BYTES = 4 * 1024 * 1024
MAX_RESULT_BYTES = 16 * 1024
MAX_RECORDED_STEPS = 16
MAX_PROBES = 8
SPIRAL_POINTS_PER_CLASS = 96
SPIRAL_TURNS = 1.5
ERROR_MESSAGE_CHARACTERS = 500

_JOB_FIELDS = frozenset(
    ("contractVersion", "kind", "template", "config", "tracePath", "resultPath", "maxTraceBytes", "maxResultBytes")
)
_CONFIG_FIELDS = frozenset(("dataset", "learningRate", "residual", "sharedBlock", "depth", "hidden", "steps", "seed"))
_STEP_DEFINITION = {
    "batch": "full",
    "loss": "mean binary cross-entropy with logits",
    "gradients": "zero_grad(set_to_none=True) before backward; no clipping, no accumulation",
    "optimizer": "SGD",
    "momentum": 0,
    "weightDecay": 0,
}


class ResultTooLargeError(ValueError):
    """An output over its cap: the "result_too_large" error category."""


def _require_fields(value, fields, where):
    if not isinstance(value, dict):
        raise JobError(f"{where} is not a JSON object.")
    if missing := sorted(fields - value.keys()):
        raise JobError(f"{where} is missing {', '.join(missing)}.")
    if extra := sorted(value.keys() - fields):
        raise JobError(f"{where} has unknown fields {', '.join(extra)}.")


def _require_int(value, name, low, high):
    if not is_int(value):
        raise JobError(f"{name} is not an integer.")
    if not low <= value <= high:
        raise JobError(f"{name} {value} is outside {low}..{high}.")
    return value


def _inside(path, directory, name):
    if not isinstance(path, str) or not path:
        raise JobError(f"{name} is not a path.")
    if os.path.dirname(os.path.realpath(path)) != os.path.realpath(directory):
        raise JobError(f"{name} is not inside the job's directory.")
    return path


def validate_job(job, job_directory):
    """The job as the script uses it, or JobError: exact keys at every level, typed and in-range knobs."""
    _require_fields(job, _JOB_FIELDS, "The job")
    if not is_int(job["contractVersion"]) or job["contractVersion"] != CONTRACT_VERSION:
        raise JobError(f"The job's contractVersion is not {CONTRACT_VERSION}.")
    if job["kind"] != KIND:
        raise JobError(f"The job's kind is not {KIND}.")
    if job["template"] != TEMPLATE:
        raise JobError(f"The job's template is not {TEMPLATE}.")
    config = job["config"]
    _require_fields(config, _CONFIG_FIELDS, "The job's config")
    if config["dataset"] not in DATASETS:
        raise JobError(f"The dataset is not one of {', '.join(DATASETS)}.")
    rate = config["learningRate"]
    if not isinstance(rate, (int, float)) or isinstance(rate, bool) or not math.isfinite(rate):
        raise JobError("learningRate is not a finite number.")
    low, high = KNOB_BOUNDS["learningRate"]
    if not low <= rate <= high:
        raise JobError(f"learningRate {rate} is outside {low}..{high}.")
    for name in ("residual", "sharedBlock"):
        if not isinstance(config[name], bool):
            raise JobError(f"{name} is not a boolean.")
    for name in ("depth", "hidden", "steps", "seed"):
        _require_int(config[name], name, *KNOB_BOUNDS[name])
    trace_path = _inside(job["tracePath"], job_directory, "tracePath")
    result_path = _inside(job["resultPath"], job_directory, "resultPath")
    if os.path.realpath(trace_path) == os.path.realpath(result_path):
        raise JobError("tracePath and resultPath are the same file.")
    return {
        "config": {**config, "learningRate": float(rate)},
        "tracePath": trace_path,
        "resultPath": result_path,
        "maxTraceBytes": _require_int(job["maxTraceBytes"], "maxTraceBytes", 1, MAX_TRACE_BYTES),
        "maxResultBytes": _require_int(job["maxResultBytes"], "maxResultBytes", 1, MAX_RESULT_BYTES),
    }


def recorded_steps(steps, diverged_at=None):
    """The 0-based steps a run records: all up to 16, else 16 spread over the run; on divergence at t, those before t
    and t."""
    if steps <= MAX_RECORDED_STEPS:
        chosen = list(range(steps))
    else:
        # (steps - 1) * i / 15 is never an exact half, so round() has no tie to break.
        chosen = [round(i * (steps - 1) / (MAX_RECORDED_STEPS - 1)) for i in range(MAX_RECORDED_STEPS)]
    if diverged_at is None:
        return chosen
    return [step for step in chosen if step < diverged_at] + [diverged_at]


def dataset(name):
    """(x, y) as lists: fixed points, independent of the seed."""
    if name == "xor":
        x = [[-1.0, -1.0], [-1.0, 1.0], [1.0, -1.0], [1.0, 1.0]]
        return x, [0.0, 1.0, 1.0, 0.0]
    # Two interleaved spirals: point i of class c at index 2i + c, the second arm the first turned by half a circle.
    x, y = [], []
    for i in range(SPIRAL_POINTS_PER_CLASS):
        t = i / (SPIRAL_POINTS_PER_CLASS - 1)
        radius = 0.1 + 0.9 * t
        angle = SPIRAL_TURNS * 2 * math.pi * t
        for label in (0, 1):
            turned = angle + label * math.pi
            x.append([radius * math.cos(turned), radius * math.sin(turned)])
            y.append(float(label))
    return x, y


def probes(count):
    """The rows a trace shows: all of a small batch, else MAX_PROBES spread from first to last."""
    if count <= MAX_PROBES:
        return list(range(count))
    return [round(i * (count - 1) / (MAX_PROBES - 1)) for i in range(MAX_PROBES)]


def _clean(value):
    if isinstance(value, list):
        return [_clean(item) for item in value]
    return value if math.isfinite(value) else None


def _numbers(tensor):
    """A tensor as nested lists of float64, a non-finite cell as None."""
    return _clean(tensor.detach().tolist())


def build_model(config):
    """The template's modules, initialized by torch's defaults under torch.manual_seed(seed)."""
    import torch

    hidden, depth = config["hidden"], config["depth"]
    torch.manual_seed(config["seed"])
    model = torch.nn.Module()
    model.embed = torch.nn.Linear(2, hidden, dtype=torch.float64)
    if config["sharedBlock"]:
        model.block = torch.nn.Linear(hidden, hidden, dtype=torch.float64)
    else:
        model.blocks = torch.nn.ModuleList(torch.nn.Linear(hidden, hidden, dtype=torch.float64) for _ in range(depth))
    model.head = torch.nn.Linear(hidden, 1, dtype=torch.float64)
    return model


def occurrences(config):
    """(module path, visit) per occurrence, in execution order."""
    depth = config["depth"]
    if config["sharedBlock"]:
        blocks = [("block", visit) for visit in range(depth)]
    else:
        blocks = [(f"blocks.{index}", 0) for index in range(depth)]
    return [("embed", 0), *blocks, ("head", 0)]


def forward(model, x, config):
    """(logits, observations): one observation per occurrence, its output retaining its gradient. A shared visit
    consumes fresh view_as aliases of the block's weight and bias, retaining theirs: that visit's gradient."""
    import torch
    import torch.nn.functional as F  # noqa: N812 - torch's own convention

    observations = []
    h = torch.tanh(model.embed(x))
    h.retain_grad()
    observations.append({"input": x, "output": h})
    for visit in range(config["depth"]):
        if config["sharedBlock"]:
            weight = model.block.weight.view_as(model.block.weight)
            bias = model.block.bias.view_as(model.block.bias)
            weight.retain_grad()
            bias.retain_grad()
        else:
            weight, bias = model.blocks[visit].weight, model.blocks[visit].bias
        output = torch.tanh(F.linear(h, weight, bias))
        output.retain_grad()
        observation = {"input": h, "output": output, "aliases": (weight, bias)}
        if config["residual"]:
            h = h + output
            observation["stream"] = h
        else:
            h = output
        observations.append(observation)
    logits = model.head(h)
    logits.retain_grad()
    observations.append({"input": h, "output": logits})
    return logits, observations


def _loss_record(loss, logits, y, rows) -> dict[str, Any]:
    import torch

    finite = bool(torch.isfinite(logits).all())
    accuracy = float(((logits[:, 0] > 0).to(y.dtype) == y).to(y.dtype).mean()) if finite else None
    return {"value": _clean(loss.item()), "probeLogits": _numbers(logits[rows, 0]), "accuracy": accuracy}


def _forward_record(observations, rows):
    records = []
    for index, observation in enumerate(observations):
        record = {
            "o": index,
            "input": _numbers(observation["input"][rows]),
            "output": _numbers(observation["output"][rows]),
        }
        if "stream" in observation:
            record["stream"] = _numbers(observation["stream"][rows])
        records.append(record)
    return records


def _step_record(step, observations, loss, logits, y, rows) -> dict[str, Any]:
    """Built only for a kept or diverged step: the backward pass leaves the activations it reads unchanged."""
    return {"step": step, "forward": _forward_record(observations, rows), "loss": _loss_record(loss, logits, y, rows)}


def _backward_record(observations, parameters, config, rows):
    visit_grads = []
    if config["sharedBlock"]:
        names = [name for name, _ in parameters]
        shared = (names.index("block.weight"), names.index("block.bias"))
        for o, observation in enumerate(observations):
            for p, alias in zip(shared, observation.get("aliases", ()), strict=False):
                visit_grads.append({"o": o, "p": p, "grad": _numbers(alias.grad)})
    return {
        "outputGrads": [{"o": o, "grad": _numbers(item["output"].grad[rows])} for o, item in enumerate(observations)],
        "parameterGrads": [{"p": p, "grad": _numbers(parameter.grad)} for p, (_, parameter) in enumerate(parameters)],
        "visitGrads": visit_grads,
    }


def train(config, x, y, after_backward=None) -> tuple[dict[str, Any], dict[str, Any]]:
    """(trace, result) as JSON-ready dicts. ``after_backward(model, step)`` runs after each backward pass: a test seam
    that can poison a gradient."""
    import torch
    import torch.nn.functional as F  # noqa: N812 - torch's own convention

    torch.set_num_threads(1)
    torch.use_deterministic_algorithms(True)
    inputs = torch.tensor(x, dtype=torch.float64)
    targets = torch.tensor(y, dtype=torch.float64)
    rows = probes(len(x))
    model = build_model(config)
    parameters = list(model.named_parameters())
    optimizer = torch.optim.SGD(model.parameters(), lr=config["learningRate"], momentum=0, weight_decay=0)
    chosen = set(recorded_steps(config["steps"]))
    recorded = []
    outcome = {"kind": "finished", "atStep": None, "updatesApplied": config["steps"]}
    final: dict[str, Any] | None = None
    for step in range(config["steps"]):
        logits, observations = forward(model, inputs, config)
        loss = F.binary_cross_entropy_with_logits(logits[:, 0], targets)
        if not math.isfinite(loss.item()):
            # (a) No backward and no update ran.
            record = _step_record(step, observations, loss, logits, targets, rows)
            recorded.append({**record, "backward": None, "update": None})
            outcome = {"kind": "diverged", "atStep": step, "updatesApplied": step}
            final = record["loss"]
            break
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        if after_backward is not None:
            after_backward(model, step)
        if not all(bool(torch.isfinite(parameter.grad).all()) for _, parameter in parameters):
            # (b) A finite loss but a non-finite gradient: no update ran.
            record = _step_record(step, observations, loss, logits, targets, rows)
            backward = _backward_record(observations, parameters, config, rows)
            recorded.append({**record, "backward": backward, "update": None})
            outcome = {"kind": "diverged", "atStep": step, "updatesApplied": step}
            final = record["loss"]
            break
        if step not in chosen:
            optimizer.step()
            continue
        record = _step_record(step, observations, loss, logits, targets, rows)
        record["backward"] = _backward_record(observations, parameters, config, rows)
        before = [parameter.detach().clone() for _, parameter in parameters]
        optimizer.step()
        record["update"] = [
            {"p": p, "before": _numbers(before[p]), "after": _numbers(parameter)}
            for p, (_, parameter) in enumerate(parameters)
        ]
        recorded.append(record)
    if final is None:
        # The weights after the last update: one more forward, its graph discarded.
        logits, _ = forward(model, inputs, config)
        final = _loss_record(F.binary_cross_entropy_with_logits(logits[:, 0], targets), logits, targets, rows)
    trace = {
        "formatVersion": FORMAT_VERSION,
        "template": TEMPLATE,
        "torchVersion": torch.__version__,
        "device": "cpu",
        "dtype": "float64",
        "threads": 1,
        "config": config,
        "stepDefinition": {**_STEP_DEFINITION, "learningRate": config["learningRate"]},
        "data": {"count": len(x), "probes": rows, "x": _clean(inputs.tolist()), "y": _clean(targets.tolist())},
        "modules": [
            # forward records embed and each block after its tanh, the head at its Linear output.
            {"path": path, "class": type(module).__name__, "outputActivation": None if path == "head" else "tanh"}
            for path, module in model.named_modules()
            if isinstance(module, torch.nn.Linear)
        ],
        "parameters": [
            {"id": name, "module": name.rsplit(".", 1)[0], "shape": list(parameter.shape)}
            for name, parameter in parameters
        ],
        "occurrences": [{"module": module, "visit": visit} for module, visit in occurrences(config)],
        "recorded": recorded,
        "outcome": outcome,
    }
    result = {
        "formatVersion": FORMAT_VERSION,
        "template": TEMPLATE,
        "config": config,
        "outcome": outcome,
        "finalLoss": final["value"],
        "finalAccuracy": final["accuracy"],
    }
    return trace, result


def serialized(value, cap, name):
    """Compact JSON bytes, shortest round-trip floats; ResultTooLargeError over ``cap``."""
    data = json.dumps(value, allow_nan=False, separators=(",", ":")).encode()
    if len(data) > cap:
        raise ResultTooLargeError(f"The {name} is {len(data)} bytes, over its cap of {cap}.")
    return data


def emit(event, **fields):
    write_frame(event, fields)


def _failure(error):
    if isinstance(error, JobError):
        category = "contract"
    elif isinstance(error, ResultTooLargeError):
        category = "result_too_large"
    else:
        category = "internal"
    return category, f"{type(error).__name__}: {str(error)[:ERROR_MESSAGE_CHARACTERS]}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    arguments = parser.parse_args()

    # Outputs are written only at the end, so a cancelled run has nothing worth keeping.
    signal.signal(signal.SIGTERM, lambda _signum, _frame: os._exit(CANCELLED_EXIT_CODE))
    os.umask(0o077)
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    import torch

    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    emit("handshake", contractVersion=CONTRACT_VERSION, torch=torch.__version__)
    try:
        emit("phase", phase="loading")
        with open(arguments.config, encoding="utf-8") as handle:
            job = validate_job(json.load(handle), os.path.dirname(os.path.abspath(arguments.config)))
        x, y = dataset(job["config"]["dataset"])
        emit("phase", phase="training")
        trace, result = train(job["config"], x, y)
        emit("phase", phase="saving")
        trace_bytes = serialized(trace, job["maxTraceBytes"], "trace")
        result_bytes = serialized(result, job["maxResultBytes"], "result")
        with open(job["tracePath"], "wb") as handle:
            handle.write(trace_bytes)
        with open(job["resultPath"], "wb") as handle:
            handle.write(result_bytes)
    except Exception as error:
        category, message = _failure(error)
        emit("error", category=category, message=message)
        return 1
    emit("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
