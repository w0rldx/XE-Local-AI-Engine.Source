#!/usr/bin/env python3

import importlib.util
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "run_scheduling_grid.py"
SPEC = importlib.util.spec_from_file_location("run_scheduling_grid", MODULE_PATH)
grid = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(grid)


def result(name: str, rates: float, rss: int = 100, gpu: int = 100, gate: bool = True, correct: bool = True):
    return {
        "name": name,
        "backend": "cuda",
        "role": "embedding",
        "token_gate_passed": gate,
        "correctness_passed": correct,
        "peak_rss_bytes": rss,
        "peak_global_gpu_used_mib": gpu,
        "scenarios": {
            key: {"median_items_per_second": rates, "deterministic_response": True}
            for key in ("short", "median", "max", "large", "concurrent")
        },
    }


class SchedulingGridTests(unittest.TestCase):
    def test_output_equivalence_uses_lane3_numeric_tolerance(self):
        self.assertTrue(grid.outputs_equivalent({"data": [0.123450]}, {"data": [0.123459]}))
        self.assertFalse(grid.outputs_equivalent({"data": [0.123450]}, {"data": [0.123461]}))

    def test_grid_varies_every_required_scheduling_dimension(self):
        self.assertGreater(len({item[1] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[2] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[3] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[4] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[5] for item in grid.CONFIGS}), 1)

    def test_evaluator_rejects_memory_regression_despite_throughput_gain(self):
        data = [result("baseline", 100), result("fast", 125, rss=106)]
        data += [
            {**item, "backend": backend, "role": role}
            for backend, role in (("cuda", "reranker"), ("cpu", "embedding"), ("cpu", "reranker"))
            for item in (result("baseline", 100), result("fast", 125, rss=106))
        ]
        decision = grid.evaluate(data)
        self.assertEqual("no-change", decision["outcome"])
        self.assertFalse(decision["ship_production_tuning"])


if __name__ == "__main__":
    unittest.main()
