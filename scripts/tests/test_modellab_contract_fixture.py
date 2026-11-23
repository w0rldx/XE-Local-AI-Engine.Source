#!/usr/bin/env python3
"""The Model Lab C#<->Python contract fixture: trace, lens, routing, attention and result bytes from the real writers in
tools/training/modellablib.py, checked against the committed copy the C# parser reads (``ModelLabTraceContractTests``).

Outside tools/training on purpose: the fixture belongs to the host's tests, not to the shipped runtime. Synthetic
values only. After a writer change, regenerate it with
``python3 scripts/tests/test_modellab_contract_fixture.py --write`` and commit the result beside the C# change.

The result dict's keys are assembled in ``modellab.trace``, which needs torch; ``_result`` below mirrors them, so a
key renamed there reaches this fixture only when ``_result`` is updated with it.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURE = REPO_ROOT / "XE-Local-AI-Engine.Tests" / "Fixtures" / "model-lab-contract.json"
MODULE_PATH = REPO_ROOT / "tools" / "training" / "modellablib.py"
SPEC = importlib.util.spec_from_file_location("modellablib", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {MODULE_PATH}")
LIB: Any = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LIB)

CAP = 100_000
HEADS = 2
TEXTS = {11: "a", 12: " b", 13: "é"}
# Block 1 of 2 has a router choosing 2 of 4 experts; per step the ids and the bfloat16 bits of 0.5, 0.25, 1.0 and 0.125.
ROUTING_IDS = ([[3, 0]], [[1, 2]])
ROUTING_BITS = ([[0x3F00, 0x3E80]], [[0x3F80, 0x3E00]])


def _written(write) -> str:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "lines.jsonl"
        write(str(path))
        return path.read_text(encoding="utf-8")


def _write_trace(path: str) -> None:
    writer = LIB.CappedTraceWriter(path, CAP)
    for step in range(2):
        for layer in (0, 1):
            norm = float("nan") if (step, layer) == (1, 1) else 3.5 + layer
            writer.write(LIB.trace_record(step, layer, "F", norm, [-1.0, 0.0, 1.0], [7, 8], [0.5, -0.25]))
    lens = writer.section(CAP, "lensFooter")
    lens.write(
        LIB.lens_record(0, [[11, 12], [12, 11]], [[-0.1, -2.5], [-0.2, -1.9]], [-0.1, -0.2], [1, 1], [0.3, 0.4], TEXTS)
    )
    # A masked chosen token: its logprob and rank are null together.
    lens.write(
        LIB.lens_record(
            1, [[12, 13], [13, 12]], [[-0.3, -1.4], [-0.5, -0.9]], [-0.3, float("-inf")], [1, 7], [0.2, 0.6], TEXTS
        )
    )
    lens.close()
    routing = writer.section(CAP, "routingFooter")
    for step, (ids, bits) in enumerate(zip(ROUTING_IDS, ROUTING_BITS, strict=True)):
        routing.write(LIB.routing_record(step, ids, bits))
    routing.close()
    writer.close()


def _write_attention(path: str) -> None:
    writer = LIB.CappedTraceWriter(path, CAP)
    for step in range(2):
        writer.write(LIB.attention_record(step, 1, 2 + step, [[2, 0], [1, 2]], [[0.6, 0.25], [0.5, 0.123456]]))
    writer.close()


def _result() -> dict[str, Any]:
    def step(chosen: float, ids: list[int]) -> tuple[float, list[int], list[str], list[float]]:
        return chosen, ids, [TEXTS[token] for token in ids], [chosen, chosen - 1.5]

    return {
        "promptTokens": 3,
        "tokens": [
            LIB.scored_token(11, TEXTS[11], step(-0.1, [11, 12]), step(-0.05, [11, 12])),
            LIB.scored_token(12, TEXTS[12], step(-0.3, [12, 13]), step(-0.25, [12, 13])),
        ],
        "text": TEXTS[11] + TEXTS[12],
        "seconds": 1.25,
        "peakVramBytes": 1000,
        "blockCount": 2,
        "modelClass": "SyntheticForCausalLM",
        "device": "synthetic-device",
        "torch": "2.0",
        "transformers": "5.0",
        "python": "3.14",
        "cuda": "12.0",
        "layers": [0, 1],
        "seed": 1234,
        "maxNewTokens": 2,
        "promptPieces": [{"id": token, "text": TEXTS[token]} for token in (11, 12, 13)],
        "layerKinds": "FF",
        "attnImplementation": "sdpa",
        "warm": False,
        "loadSeconds": 2.5,
        "lens": {
            "layers": [0, 1],
            "topTokens": 2,
            "records": 2,
            "truncated": False,
            "tiedEmbeddings": True,
            "vocabSize": 16,
        },
        "routing": {
            "layers": [1],
            "experts": 4,
            "expertsPerToken": 2,
            "normalizedTopK": False,
            "weightEncoding": LIB.ROUTING_WEIGHT_ENCODING,
            "records": 2,
            "truncated": False,
        },
        "attention": {
            "layers": [1],
            "heads": HEADS,
            "kvHeads": 1,
            "headDim": 8,
            "topKeys": 2,
            "weightScale": LIB.ATTENTION_WEIGHT_SCALE,
            "records": 2,
            "truncated": False,
        },
    }


def build() -> dict[str, Any]:
    """Every file as the writers write it, kept as text so the C# side reads the exact bytes."""
    return {
        "contractVersion": LIB.CONTRACT_VERSION,
        "trace": _written(_write_trace),
        "attention": _written(_write_attention),
        "result": LIB.fitted_trace_result(_result()).decode(),
    }


class ContractFixtureTests(unittest.TestCase):
    def test_the_committed_fixture_is_what_the_writers_write_now(self) -> None:
        committed = json.loads(FIXTURE.read_text(encoding="utf-8"))
        self.assertEqual(
            build(),
            committed,
            "The writers changed: run `python3 scripts/tests/test_modellab_contract_fixture.py --write` and keep the "
            "C# parser in step.",
        )

    def test_the_fixture_is_strict_json_throughout(self) -> None:
        def refuse(token: str) -> None:
            raise AssertionError(f"{token} is not JSON.")

        fixture = build()
        for name in ("trace", "attention"):
            for line in fixture[name].splitlines():
                json.loads(line, parse_constant=refuse)
        json.loads(fixture["result"], parse_constant=refuse)


if __name__ == "__main__":
    if sys.argv[1:] == ["--write"]:
        FIXTURE.write_text(json.dumps(build(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    else:
        unittest.main()
