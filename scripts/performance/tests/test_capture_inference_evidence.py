#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "capture_inference_evidence.py"
SPEC = importlib.util.spec_from_file_location("capture_inference_evidence", MODULE_PATH)
assert SPEC and SPEC.loader
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class CaptureInferenceEvidenceTests(unittest.TestCase):
    def test_baseline_rejects_identity_hash_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            payload = root / "model.gguf"
            payload.write_text("model", encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "hash mismatch"):
                capture.verify_identity("model", {"path": str(payload), "sha256": "0" * 64})

    def test_command_capture_computes_median_and_nearest_rank_p95(self) -> None:
        result = capture.run_command({
            "name": "deterministic-json",
            "argv": ["/bin/sh", "-c", "printf '{\"throughput\": 42, \"latency_ms\": 10}'"],
            "warmups": 1,
            "repeats": 3,
            "timeout_seconds": 5,
        }, "command")
        self.assertEqual(42, result["aggregates"]["throughput"]["median"])
        self.assertEqual(10, result["aggregates"]["latency_ms"]["p95"])
        self.assertEqual(3, len(result["runs"]))
        self.assertEqual(1, len(result["warmup_results"]))

    def test_llama_bench_json_is_projected_to_role_metrics(self) -> None:
        stdout = json.dumps([
            {"n_prompt": 128, "n_gen": 0, "embeddings": False, "avg_ts": 1000.0},
            {"n_prompt": 0, "n_gen": 32, "embeddings": False, "avg_ts": 80.0},
            {"n_prompt": 512, "n_gen": 0, "embeddings": True, "avg_ts": 2400.0},
        ])
        self.assertEqual({
            "prompt_tokens_per_second": 1000.0,
            "generation_tokens_per_second": 80.0,
            "embedding_tokens_per_second": 2400.0,
        }, capture.numeric_metrics(stdout))

    def test_baseline_captures_framework_identity_and_explicit_gaps(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            model = root / "model.gguf"
            corpus = root / "corpus.json"
            runtime = root / "llama-server"
            model.write_text("model", encoding="utf-8")
            corpus.write_text("corpus", encoding="utf-8")
            runtime.write_text("#!/bin/sh\nprintf 'fake runtime\\n'\n", encoding="utf-8")
            runtime.chmod(0o755)
            spec = {
                "schema_version": "1.0", "kind": "inference-benchmark-capture", "capture_id": "baseline-test", "phase": "baseline",
                "models": [{"name": "dense", "role": "chat", "quant": "Q4_K_M", "path": str(model), "sha256": digest(model)}],
                "corpus": {"name": "golden", "path": str(corpus), "sha256": digest(corpus)},
                "runtime": {"tag": "b9692", "provenance": "managed-source-build", "backend": "cuda", "path": str(runtime), "sha256": digest(runtime)},
                "framework": {"source_commit": "1234567", "maf_version": "1.15.0", "meai_version": "10.8.1", "openai_version": "2.12.0"},
                "benchmark": {"cache_state": "cold", "cache_preparation": "delete cache", "ambient_load_policy": "idle", "acceptance_rule": "median/p95"},
                "coverage": {"unvalidated": [{"target": "Vulkan", "reason": "No ICD"}]},
                "commands": [{"name": "chat", "argv": ["/bin/sh", "-c", "printf '{\"ttft_ms\": 10}'"], "warmups": 0, "repeats": 2, "timeout_seconds": 5}],
            }
            spec_path, output = root / "spec.json", root / "output.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")
            capture.capture_baseline(spec_path, output)
            artifact = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("1.15.0", artifact["framework"]["maf_version"])
            self.assertEqual("Vulkan", artifact["coverage"]["unvalidated"][0]["target"])
            self.assertEqual(10, artifact["commands"][0]["aggregates"]["ttft_ms"]["median"])

    def test_fit_capture_proves_default_verbose_helper_and_replay_vectors(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            server = root / "llama-server"
            helper = root / "llama-fit-params"
            server.write_text("#!/bin/sh\nprintf 'server output\\n'\n", encoding="utf-8")
            helper.write_text("#!/bin/sh\nprintf '%s\\n' '-c 4096 -ngl 99 -ts 1.0 -ctk q8_0 -ctv q8_0'\n", encoding="utf-8")
            server.chmod(0o755)
            helper.chmod(0o755)
            common = [str(server), "-m", "model.gguf", "--fit"]
            spec = {
                "schema_version": "1.0", "kind": "fit-replay-capture", "capture_id": "fit-test", "phase": "fit-proof",
                "binaries": {
                    "server": {"path": str(server), "sha256": digest(server)},
                    "fit_helper": {"path": str(helper), "sha256": digest(helper)},
                },
                "commands": {
                    "default_verbosity": {"name": "default", "argv": common, "repeats": 1, "timeout_seconds": 2},
                    "verbose": {"name": "verbose", "argv": common + ["-v"], "repeats": 1, "timeout_seconds": 2},
                    "fit_params": {"name": "fit", "argv": [str(helper), "-m", "model.gguf"], "repeats": 1, "timeout_seconds": 2},
                    "explore": {"name": "explore", "argv": common + ["--parallel", "1"], "repeats": 1, "timeout_seconds": 2},
                    "replay": {
                        "name": "replay",
                        "argv": [str(server), "-m", "model.gguf", "--parallel", "1", "-c", "4096", "-ngl", "99", "-ts", "1.0", "-ctk", "q8_0", "-ctv", "q8_0"],
                        "repeats": 1,
                        "timeout_seconds": 2,
                    },
                },
                "launch_vectors": {
                    "explore": ["-m", "model.gguf", "--fit", "--parallel", "1"],
                    "replay": ["-m", "model.gguf", "--parallel", "1", "-c", "4096", "-ngl", "99", "-ts", "1.0", "-ctk", "q8_0", "-ctv", "q8_0"],
                },
                "resource_acceptance": {"max_delta_percent": 10000},
                "coverage": {"unvalidated": []},
            }
            spec_path = root / "spec.json"
            output = root / "result.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")
            capture.capture_fit(spec_path, output)
            artifact = json.loads(output.read_text(encoding="utf-8"))
            self.assertTrue(artifact["equivalence"]["placement_equal"])
            self.assertTrue(artifact["equivalence"]["non_fit_vector_equal"])
            self.assertNotIn("-v", artifact["captures"]["default_verbosity"]["argv"])
            self.assertIn("-v", artifact["captures"]["verbose"]["argv"])
            self.assertEqual(artifact["launch_vectors"]["explore"], artifact["captures"]["explore"]["argv"][1:])
            self.assertEqual(artifact["launch_vectors"]["replay"], artifact["captures"]["replay"]["argv"][1:])

    def test_compare_rejects_changed_runtime_identity(self) -> None:
        baseline = {
            "kind": "inference-benchmark-evidence",
            "verified_identity": {"models": [], "corpus": {}, "runtime": {"tag": "a", "provenance": "source", "backend": "cuda", "sha256": "1"}},
            "host": {}, "commands": [],
        }
        candidate = json.loads(json.dumps(baseline))
        candidate["verified_identity"]["runtime"]["tag"] = "b"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            first, second, output = root / "first.json", root / "second.json", root / "output.json"
            first.write_text(json.dumps(baseline), encoding="utf-8")
            second.write_text(json.dumps(candidate), encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "identity differs"):
                capture.compare_artifacts(first, second, output)

    def test_sanitize_replaces_absolute_paths_and_rejects_unmapped_home(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(json.dumps({"path": "/home/operator/models/model.gguf", "argv": ["/opt/runtime/llama-server"]}), encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "user-home path"):
                capture.sanitize_artifact(source, output, ["/opt/runtime=$RUNTIME_ROOT"])
            capture.sanitize_artifact(
                source,
                output,
                ["/home/operator/models=$MODEL_ROOT", "/opt/runtime=$RUNTIME_ROOT"],
            )
            sanitized = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("$MODEL_ROOT/model.gguf", sanitized["path"])
            self.assertEqual("$RUNTIME_ROOT/llama-server", sanitized["argv"][0])
            self.assertTrue(sanitized["sanitized"])


if __name__ == "__main__":
    unittest.main()
