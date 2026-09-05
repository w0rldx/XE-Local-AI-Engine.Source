#!/usr/bin/env python3

import importlib.util
import unittest
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import patch

MODULE_PATH = Path(__file__).parents[1] / "run_scheduling_grid.py"
SPEC = importlib.util.spec_from_file_location("run_scheduling_grid", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
grid = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(grid)


def result(
    name: str,
    rates: float,
    *,
    backend: str = "cuda",
    role: str = "embedding",
    output=None,
    rss: int = 100,
    gpu_status: str = "measured",
    gpu: int = 100,
    gate: bool = True,
    deterministic: bool = True,
    readback: bool = True,
) -> dict[str, Any]:
    canonical = output if output is not None else {"data": [{"index": 0, "value": [0.1, 0.2]}]}
    residency: dict[str, Any] = {"status": gpu_status}
    if gpu_status == "measured":
        residency["peak_used_mib"] = gpu
    return {
        "name": name,
        "backend": backend,
        "role": role,
        "token_gate": {"passed": gate},
        "context_readback": {"status": "measured" if readback else "missing"},
        "repeat_determinism_passed": deterministic,
        "peak_rss_bytes": rss,
        "process_gpu_residency": residency,
        "scenarios": {
            key: {
                "median_items_per_second": rates,
                "repeat_deterministic": deterministic,
                "_canonical_output": canonical,
            }
            for key in grid.SCENARIOS
        },
    }


def complete_grid(candidate_factory):
    values = []
    for backend in ("cuda", "cpu"):
        for role in ("embedding", "reranker"):
            values.append(result("baseline", 100, backend=backend, role=role))
            values.append(candidate_factory(backend, role))
    return values


class SchedulingGridTests(unittest.TestCase):
    def test_output_equivalence_uses_numeric_tolerance_and_preserves_order(self):
        self.assertTrue(
            grid.outputs_equivalent(
                {"data": [{"index": 0, "value": [0.123450, 2]}]},
                {"data": [{"index": 0, "value": [0.123459, 2.0]}]},
            )
        )
        self.assertFalse(
            grid.outputs_equivalent(
                {"data": [{"index": 0}, {"index": 1}]},
                {"data": [{"index": 1}, {"index": 0}]},
            )
        )
        self.assertFalse(
            grid.outputs_equivalent(
                {"data": [0.123450]},
                {"data": [0.123461]},
            )
        )

    def test_distinct_embedding_inputs_make_reordering_observable(self):
        inputs = grid.distinct_inputs(
            "search_document: ",
            "local inference",
            4,
            "short",
        )
        self.assertEqual(4, len(set(inputs)))
        ordered = {"data": [{"input": value} for value in inputs]}
        reordered = {"data": list(reversed(ordered["data"]))}
        self.assertFalse(grid.outputs_equivalent(ordered, reordered))

    def test_scenario_records_repeat_determinism_positive_and_negative(self):
        stable = grid.scenario(
            lambda _port, _items: {"data": [0.25]},
            0,
            ["input"],
            3,
        )
        values = iter(
            [
                {"data": [0.25]},
                {"data": [0.25]},
                {"data": [0.25]},
                {"data": [0.5]},
            ]
        )
        unstable = grid.scenario(
            lambda _port, _items: next(values),
            0,
            ["input"],
            3,
        )
        self.assertTrue(stable["repeat_deterministic"])
        self.assertFalse(unstable["repeat_deterministic"])

    def test_concurrent_scenario_preserves_batch_order_and_item_count(self):
        batches = [["first-a", "first-b"], ["second-a", "second-b"]]
        measured = grid.scenario(
            lambda _port, items: {"data": list(items)},
            0,
            batches,
            2,
            concurrent=True,
        )
        self.assertTrue(measured["repeat_deterministic"])
        self.assertEqual(
            [
                {"data": ["first-a", "first-b"]},
                {"data": ["second-a", "second-b"]},
            ],
            measured["_canonical_output"],
        )
        self.assertGreater(measured["median_items_per_second"], 0)

    def test_evaluator_accepts_only_baseline_equivalent_deterministic_outputs(self):
        data = complete_grid(
            lambda backend, role: result(
                "fast",
                125,
                backend=backend,
                role=role,
                output={"data": [{"index": 0, "value": [0.100009, 0.2]}]},
            )
        )
        decision = grid.evaluate(data)
        self.assertTrue(decision["ship_production_tuning"])
        self.assertTrue(all(item["qualifies"] for item in decision["comparisons"]))

    def test_evaluator_rejects_candidate_output_reordering(self):
        data = complete_grid(
            lambda backend, role: result(
                "fast",
                125,
                backend=backend,
                role=role,
                output={
                    "data": [
                        {"index": 1, "value": [0.1]},
                        {"index": 0, "value": [0.2]},
                    ]
                },
            )
        )
        for item in data:
            if item["name"] == "baseline":
                item["scenarios"] = {
                    key: {
                        "median_items_per_second": 100,
                        "repeat_deterministic": True,
                        "_canonical_output": {
                            "data": [
                                {"index": 0, "value": [0.2]},
                                {"index": 1, "value": [0.1]},
                            ]
                        },
                    }
                    for key in grid.SCENARIOS
                }
        decision = grid.evaluate(data)
        self.assertTrue(all(not item["qualifies"] for item in decision["comparisons"]))
        self.assertTrue(
            all(not all(item["semantic_equivalence_to_baseline"].values()) for item in decision["comparisons"])
        )

    def test_evaluator_rejects_repeat_nondeterminism_even_when_output_matches(self):
        data = complete_grid(
            lambda backend, role: result(
                "fast",
                125,
                backend=backend,
                role=role,
                deterministic=False,
            )
        )
        decision = grid.evaluate(data)
        self.assertTrue(all(not item["qualifies"] for item in decision["comparisons"]))
        self.assertTrue(all(all(item["semantic_equivalence_to_baseline"].values()) for item in decision["comparisons"]))

    def test_evaluator_does_not_compare_rejecting_baseline(self):
        data = complete_grid(
            lambda backend, role: result(
                "fast",
                125,
                backend=backend,
                role=role,
            )
        )
        baseline = next(
            item
            for item in data
            if item["backend"] == "cuda" and item["role"] == "reranker" and item["name"] == "baseline"
        )
        baseline["token_gate"]["passed"] = False
        decision = grid.evaluate(data)
        comparison = next(
            item for item in decision["comparisons"] if item["backend"] == "cuda" and item["role"] == "reranker"
        )
        self.assertFalse(comparison["baseline_eligible"])
        self.assertFalse(comparison["comparable_to_baseline"])
        self.assertEqual(
            "baseline_role_preflight_or_runtime_rejected",
            comparison["baseline_ineligible_reason"],
        )

    def test_process_gpu_parser_ignores_unrelated_pid_contention(self):
        output = "100, 12000\n4242, 300\n999, 8000\n4242, 25\n"
        self.assertEqual(325, grid.parse_process_gpu_rows(output, 4242))

    def test_process_gpu_sample_reports_unavailable_and_zero_explicitly(self):
        unavailable = grid.gpu_process_sample(
            42,
            runner=lambda *_args, **_kwargs: SimpleNamespace(
                returncode=1,
                stdout="",
                stderr="query unsupported",
            ),
        )
        zero = grid.gpu_process_sample(
            42,
            runner=lambda *_args, **_kwargs: SimpleNamespace(
                returncode=0,
                stdout="99, 4000\n",
                stderr="",
            ),
        )
        self.assertEqual("unavailable", unavailable["status"])
        self.assertEqual("zero", zero["status"])
        self.assertEqual(0, zero["used_mib"])

    def test_process_gpu_sample_handles_missing_nvidia_smi(self):
        def missing(*_args, **_kwargs):
            raise FileNotFoundError("nvidia-smi")

        sample = grid.gpu_process_sample(42, runner=missing)
        self.assertEqual("unavailable", sample["status"])
        self.assertIn("launch failed", sample["reason"])

    def test_cpu_comparison_ignores_unrelated_gpu_state(self):
        baseline = result(
            "baseline",
            100,
            backend="cpu",
            gpu_status="unavailable",
        )
        candidate = result(
            "fast",
            125,
            backend="cpu",
            gpu_status="zero",
        )
        delta = grid.gpu_memory_delta(baseline, candidate, "cpu")
        self.assertTrue(delta["passed"])
        self.assertEqual("not_applicable", delta["status"])

    def test_cuda_comparison_fails_closed_on_zero_or_unavailable_residency(self):
        baseline = result("baseline", 100, gpu_status="measured", gpu=100)
        zero = result("fast", 125, gpu_status="zero")
        unavailable = result("fast", 125, gpu_status="unavailable")
        self.assertFalse(grid.gpu_memory_delta(baseline, zero, "cuda")["passed"])
        self.assertFalse(grid.gpu_memory_delta(baseline, unavailable, "cuda")["passed"])

    def test_role_requests_use_actual_embedding_and_reranker_routes(self):
        with patch.object(grid, "post", return_value={"ok": True}) as mocked:
            grid.embedding(19300, ["document"])
            grid.rerank(19301, ["document"])
        self.assertEqual("/v1/embeddings", mocked.call_args_list[0].args[1])
        self.assertEqual(
            {"model": "nomic", "input": ["document"]},
            mocked.call_args_list[0].args[2],
        )
        self.assertEqual("/v1/rerank", mocked.call_args_list[1].args[1])
        self.assertEqual(
            {
                "query": "Which passage discusses inference scheduling?",
                "documents": ["document"],
            },
            mocked.call_args_list[1].args[2],
        )

    def test_actual_request_preflight_records_role_route(self):
        embedding = grid.actual_request_preflight(
            lambda _port, _items: {"data": [1]},
            19300,
            ["document"],
            "embedding",
        )
        reranker = grid.actual_request_preflight(
            lambda _port, _items: {"results": [1]},
            19301,
            ["document"],
            "reranker",
        )
        self.assertEqual("accepted", embedding["status"])
        self.assertEqual("/v1/embeddings", embedding["route"])
        self.assertEqual("/v1/rerank", reranker["route"])

    def test_context_readback_requires_explicit_runtime_log_value(self):
        measured = grid.parse_context_readback("slot init: new slot, n_ctx = 1024\nnew slot, n_ctx = 768\n")
        missing = grid.parse_context_readback("server is ready\n")
        self.assertEqual("measured", measured["status"])
        self.assertEqual(768, measured["per_sequence_context"])
        self.assertEqual(
            "llama-server slot initialization log",
            measured["readback_source"],
        )
        self.assertEqual("missing", missing["status"])
        self.assertIsNone(missing["readback_source"])

    def test_extract_baseline_outputs_persists_baselines_and_strips_raw_cells(self):
        values = [
            result("baseline", 100, backend="cuda", role="embedding"),
            result("fast", 125, backend="cuda", role="embedding"),
        ]
        outputs = grid.extract_baseline_outputs(values)
        self.assertIn("short", outputs["cuda"]["embedding"])
        self.assertNotIn(
            "_canonical_output",
            values[0]["scenarios"]["short"],
        )
        self.assertNotIn(
            "_canonical_output",
            values[1]["scenarios"]["short"],
        )

    def test_grid_varies_every_required_scheduling_dimension(self):
        self.assertGreater(len({item[1] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[2] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[3] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[4] for item in grid.CONFIGS}), 1)
        self.assertGreater(len({item[5] for item in grid.CONFIGS}), 1)


if __name__ == "__main__":
    unittest.main()
