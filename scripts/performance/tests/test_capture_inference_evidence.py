#!/usr/bin/env python3
from __future__ import annotations

import ast
import copy
import hashlib
import importlib.util
import inspect
import json
import math
import os
import re
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from typing import Any
from unittest import mock

MODULE_PATH = Path(__file__).parents[1] / "capture_inference_evidence.py"
REPOSITORY_ROOT = Path(__file__).parents[3]
POLICY_SCHEMA_PATH = REPOSITORY_ROOT / "docs/performance/schemas/inference-comparison-policy.schema.json"
POLICY_EXAMPLE_PATH = REPOSITORY_ROOT / "docs/performance/policies/generic-inference-throughput-policy.example.json"
PACKAGE_PATH = MODULE_PATH.parent / "inference_evidence"
SPEC = importlib.util.spec_from_file_location("capture_inference_evidence", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(cwd: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", "-C", str(cwd), *arguments],
        text=True,
        capture_output=True,
        check=True,
    )
    return completed.stdout.strip()


def framework_baseline_spec(root: Path, repository: Path, marker: Path) -> dict:
    model = root / "model.gguf"
    corpus = root / "corpus.json"
    runtime = root / "llama-server"
    assembly = repository / "bin" / "Framework.Tests.dll"
    model.write_text("model", encoding="utf-8")
    corpus.write_text("corpus", encoding="utf-8")
    runtime.write_text("#!/bin/sh\nprintf 'fake runtime\\n'\n", encoding="utf-8")
    runtime.chmod(0o755)
    assembly.parent.mkdir()
    assembly.write_text("built assembly", encoding="utf-8")
    pins = repository / "Directory.Packages.props"
    return {
        "schema_version": "1.0",
        "kind": "inference-benchmark-capture",
        "capture_id": "framework-baseline-test",
        "phase": "baseline",
        "models": [
            {
                "name": "dense",
                "role": "chat",
                "quant": "Q4_K_M",
                "path": str(model),
                "sha256": digest(model),
            }
        ],
        "corpus": {"name": "golden", "path": str(corpus), "sha256": digest(corpus)},
        "runtime": {
            "tag": "b9692",
            "provenance": "managed-source-build",
            "backend": "cuda",
            "path": str(runtime),
            "sha256": digest(runtime),
        },
        "framework": {
            "source_commit": git(repository, "rev-parse", "HEAD"),
            "maf_version": "1.15.0",
            "meai_version": "10.8.1",
            "openai_version": "2.12.0",
            "central_package_pins": {
                "path": "Directory.Packages.props",
                "sha256": digest(pins),
            },
        },
        "benchmark": {
            "cache_state": "cold",
            "cache_preparation": "delete cache",
            "ambient_load_policy": "idle",
            "acceptance_rule": "median/p95",
        },
        "coverage": {"unvalidated": [{"target": "Vulkan", "reason": "No ICD"}]},
        "commands": [
            {
                "name": "framework-contract",
                "partition": "framework-contract",
                "comparability": "same test filter; verified framework identity is the intended variable",
                "argv": ["/bin/sh", "-c", f"printf done > {marker}; printf '{{\"latency_ms\": 10}}'"],
                "cwd": str(repository),
                "framework_assemblies": [{"name": assembly.name, "path": str(assembly), "sha256": digest(assembly)}],
                "warmups": 0,
                "repeats": 1,
                "timeout_seconds": 5,
            }
        ],
    }


def fit_capture_spec(server: Path, helper: Path) -> dict:
    base = [
        str(server),
        "-m",
        "model.gguf",
        "--host",
        "127.0.0.1",
        "--port",
        "19150",
        "--parallel",
        "1",
        "--no-warmup",
    ]
    explore = [
        *base,
        "--fit",
        "on",
        "--metrics",
        "-c",
        "4096",
        "-fa",
        "on",
        "-ctk",
        "q8_0",
        "-ctv",
        "q8_0",
        "--jinja",
        "-v",
    ]
    replay = [
        *base,
        "-c",
        "4096",
        "--n-gpu-layers",
        "99",
        "-ts",
        "1.0",
        "-ctk",
        "q8_0",
        "-ctv",
        "q8_0",
        "--flash-attn",
        "on",
        "--jinja",
        "--metrics",
    ]
    helper_argv = [
        str(helper),
        "-m",
        "model.gguf",
        "--parallel",
        "1",
        "--fit",
        "on",
        "-c",
        "4096",
        "-fa",
        "on",
        "-ctk",
        "q8_0",
        "-ctv",
        "q8_0",
    ]
    return {
        "schema_version": "1.0",
        "kind": "fit-replay-capture",
        "capture_id": "fit-test",
        "phase": "fit-proof",
        "binaries": {
            "server": {"path": str(server), "sha256": digest(server)},
            "fit_helper": {"path": str(helper), "sha256": digest(helper)},
        },
        "commands": {
            "default_verbosity": {
                "name": "default",
                "argv": explore[:-1],
                "repeats": 1,
                "timeout_seconds": 2,
            },
            "verbose": {"name": "verbose", "argv": list(explore), "repeats": 1, "timeout_seconds": 2},
            "fit_params": {"name": "fit", "argv": helper_argv, "repeats": 1, "timeout_seconds": 2},
            "explore": {"name": "explore", "argv": list(explore), "repeats": 1, "timeout_seconds": 2},
            "replay": {"name": "replay", "argv": list(replay), "repeats": 1, "timeout_seconds": 2},
        },
        "launch_vectors": {
            "explore": explore[1:],
            "replay": replay[1:],
        },
        "resource_acceptance": {"max_delta_percent": 10000},
        "coverage": {"unvalidated": []},
    }


def gate_artifact(value: float = 100.0) -> dict:
    framework = {
        "source_commit": "0123456789abcdef",
        "maf_version": "1.15.0",
        "meai_version": "10.8.1",
        "openai_version": "2.12.0",
    }
    return {
        "schema_version": "1.0",
        "kind": "inference-benchmark-evidence",
        "framework": framework,
        "verified_identity": {
            "models": [
                {
                    "name": "dense",
                    "role": "chat",
                    "quant": "Q4_K_M",
                    "sha256": "1" * 64,
                    "sha256_verified": True,
                }
            ],
            "corpus": {
                "name": "golden",
                "sha256": "2" * 64,
                "sha256_verified": True,
            },
            "runtime": {
                "tag": "b9692",
                "provenance": "managed-source-build",
                "backend": "cuda",
                "sha256": "3" * 64,
                "sha256_verified": True,
                "runtime_local_dependencies": {
                    "manifest_sha256": "4" * 64,
                },
                "auxiliary_binaries": [],
            },
            "framework": {
                "required": True,
                "verified": True,
                "declaration": copy.deepcopy(framework),
                "command_trees": [
                    {
                        "command": "chat",
                        "git_head": framework["source_commit"],
                        "git_head_verified": True,
                        "git_clean": True,
                        "declared_versions_verified": True,
                        "central_package_pins": {
                            "path": "Directory.Packages.props",
                            "sha256": "7" * 64,
                            "sha256_verified": True,
                        },
                        "assemblies": [
                            {
                                "name": "Framework.Tests.dll",
                                "path": "/home/private/bin/Framework.Tests.dll",
                                "sha256": "8" * 64,
                                "sha256_verified": True,
                            }
                        ],
                    }
                ],
            },
        },
        "host": {
            "os": "Linux",
            "kernel": "test-kernel",
            "architecture": "x86_64",
            "cpu": "test-cpu",
            "logical_cpu_count": 8,
            "runtime_devices": {
                "argv": ["/verified/llama-server", "--list-devices"],
                "available": True,
                "exit_code": 0,
                "stdout": "CUDA0: test-gpu",
                "stderr": "",
            },
        },
        "commands": [
            {
                "name": "chat",
                "argv": ["/sensitive/runtime/llama-server", "--token", "fixture-secret"],
                "argv_sha256": "5" * 64,
                "stdout": "fixture-secret-stdout",
                "stderr": "fixture-secret-stderr",
                "env": {"TOKEN": "fixture-secret-environment"},
                "framework_assemblies": [
                    {
                        "path": "/home/private/bin/Framework.Tests.dll",
                        "sha256": "6" * 64,
                    }
                ],
                "aggregates": {
                    "throughput": {
                        "median": value,
                        "p95": value,
                        "minimum": value,
                        "maximum": value,
                    }
                },
            }
        ],
    }


def gate_policy(
    *,
    threshold: float = 10.0,
    kind: str = "minimum_improvement_percent",
    statistic: str = "median",
    allowed_identity_changes: list[str] | None = None,
    rules: list[dict] | None = None,
) -> dict:
    policy: dict[str, Any] = {
        "schema_version": "1.0",
        "policy_id": "test-throughput-policy",
        "rules": rules
        if rules is not None
        else [
            {
                "id": "throughput",
                "command": "chat",
                "metric": "throughput",
                "statistic": statistic,
                "kind": kind,
                "threshold_percent": threshold,
            }
        ],
    }
    if allowed_identity_changes is not None:
        policy["allowed_identity_changes"] = allowed_identity_changes
    return policy


def add_verified_framework_command_tree(artifact: dict, marker: str) -> None:
    framework = artifact["framework"]
    framework["source_commit"] = marker * 16
    framework["maf_version"] = f"1.{marker}.0"
    identity = artifact["verified_identity"]["framework"]
    identity.update(
        {
            "required": True,
            "verified": True,
            "declaration": copy.deepcopy(framework),
            "command_trees": [
                {
                    "command": "chat",
                    "partition": "framework-contract",
                    "git_head": marker * 16,
                    "git_head_verified": True,
                    "git_clean": True,
                    "declared_versions_verified": True,
                    "central_package_pins": {
                        "path": "Directory.Packages.props",
                        "sha256": marker * 64,
                        "sha256_verified": True,
                    },
                    "resolved_package_versions": {
                        "Microsoft.Agents.AI": f"1.{marker}.0",
                        "Microsoft.Extensions.AI": framework["meai_version"],
                        "OpenAI": framework["openai_version"],
                    },
                    "assemblies": [
                        {
                            "name": "Framework.Tests.dll",
                            "path": f"/verified/{marker}/Framework.Tests.dll",
                            "sha256": marker * 64,
                            "sha256_verified": True,
                        }
                    ],
                }
            ],
        }
    )


def write_gate_inputs(
    root: Path,
    baseline: dict,
    candidate: dict,
    policy: dict,
) -> tuple[Path, Path, Path, Path]:
    baseline_path = root / "baseline.json"
    candidate_path = root / "candidate.json"
    policy_path = root / "policy.json"
    output_path = root / "verdict.json"
    baseline_path.write_text(json.dumps(baseline), encoding="utf-8")
    candidate_path.write_text(json.dumps(candidate), encoding="utf-8")
    policy_path.write_text(json.dumps(policy), encoding="utf-8")
    return baseline_path, candidate_path, policy_path, output_path


class CaptureInferenceEvidenceTests(unittest.TestCase):
    def test_baseline_rejects_identity_hash_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            payload = root / "model.gguf"
            payload.write_text("model", encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "hash mismatch"):
                capture.verify_identity("model", {"path": str(payload), "sha256": "0" * 64})

    def test_command_capture_computes_median_and_nearest_rank_p95(self) -> None:
        result = capture.run_command(
            {
                "name": "deterministic-json",
                "argv": ["/bin/sh", "-c", 'printf \'{"throughput": 42, "latency_ms": 10}\''],
                "warmups": 1,
                "repeats": 3,
                "timeout_seconds": 5,
            },
            "command",
        )
        self.assertEqual(42, result["aggregates"]["throughput"]["median"])
        self.assertEqual(10, result["aggregates"]["latency_ms"]["p95"])
        self.assertEqual(3, len(result["runs"]))
        self.assertEqual(1, len(result["warmup_results"]))

    def test_run_once_always_kills_spawned_process_group_after_leader_exits(self) -> None:
        if os.name != "posix" or not Path("/proc").is_dir():
            self.skipTest("process-group regression requires Linux /proc")
        with tempfile.TemporaryDirectory() as raw:
            child_pid_path = Path(raw) / "child.pid"
            result = capture.run_once(
                ["/bin/sh", "-c", f"sleep 30 >/dev/null 2>&1 & echo $! > {child_pid_path}"],
                timeout_seconds=5,
                expected_timeout=False,
                cwd=None,
                env=os.environ.copy(),
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            deadline = time.monotonic() + 2
            while Path(f"/proc/{child_pid}").exists() and time.monotonic() < deadline:
                time.sleep(0.02)

            self.assertTrue(result["success"])
            self.assertFalse(Path(f"/proc/{child_pid}").exists(), "spawned child process survived the evidence command")

    def test_run_once_bounds_streams_and_records_full_hashes(self) -> None:
        stdout = "x" * (capture.MAX_CAPTURE_STREAM_BYTES + 257)
        stderr = "y" * (capture.MAX_CAPTURE_STREAM_BYTES + 513)

        result = capture.run_once(
            [
                os.fspath(Path(os.sys.executable)),
                "-c",
                "import sys; sys.stdout.write('x' * int(sys.argv[1])); sys.stderr.write('y' * int(sys.argv[2]))",
                str(len(stdout)),
                str(len(stderr)),
            ],
            timeout_seconds=5,
            expected_timeout=False,
            cwd=None,
            env=os.environ.copy(),
        )

        self.assertTrue(result["success"])
        self.assertTrue(result["stdout_truncated"])
        self.assertTrue(result["stderr_truncated"])
        self.assertLessEqual(len(result["stdout"].encode()), capture.MAX_CAPTURE_STREAM_BYTES)
        self.assertLessEqual(len(result["stderr"].encode()), capture.MAX_CAPTURE_STREAM_BYTES)
        self.assertEqual(hashlib.sha256(stdout.encode()).hexdigest(), result["stdout_sha256"])
        self.assertEqual(hashlib.sha256(stderr.encode()).hexdigest(), result["stderr_sha256"])
        self.assertEqual(len(stdout), result["stdout_bytes"])
        self.assertEqual(len(stderr), result["stderr_bytes"])

    def test_run_once_uses_only_bounded_post_kill_waits(self) -> None:
        source = "\n".join(
            (
                inspect.getsource(capture.run_once),
                inspect.getsource(capture.cleanup_process_group),
            )
        )

        self.assertNotRegex(source, r"process\.wait\(\)")
        self.assertRegex(source, r"process\.wait\(timeout=")
        self.assertNotRegex(source, r"\.join\(\)")
        self.assertRegex(source, r"\.join\(timeout=")

    def test_llama_bench_json_is_projected_to_role_metrics(self) -> None:
        stdout = json.dumps(
            [
                {"n_prompt": 128, "n_gen": 0, "embeddings": False, "avg_ts": 1000.0},
                {"n_prompt": 0, "n_gen": 32, "embeddings": False, "avg_ts": 80.0},
                {"n_prompt": 512, "n_gen": 0, "embeddings": True, "avg_ts": 2400.0},
            ]
        )
        self.assertEqual(
            {
                "prompt_tokens_per_second": 1000.0,
                "generation_tokens_per_second": 80.0,
                "embedding_tokens_per_second": 2400.0,
            },
            capture.numeric_metrics(stdout),
        )

    def test_supervisor_fit_on_and_long_gpu_layers_alias_normalize_for_replay(self) -> None:
        explore = ["--fit", "on", "--metrics", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0", "--jinja", "-v"]
        replay = [
            "-c",
            "0",
            "--n-gpu-layers",
            "-1",
            "-ctk",
            "q8_0",
            "-ctv",
            "q8_0",
            "--flash-attn",
            "on",
            "--jinja",
            "--metrics",
        ]
        self.assertEqual(["--jinja"], capture.without_fit_semantics(explore))
        self.assertEqual(["--jinja"], capture.without_fit_semantics(replay))
        self.assertEqual(
            {
                "-c": ["0"],
                "-ngl": ["-1"],
                "-ctk": ["q8_0"],
                "-ctv": ["q8_0"],
                "-fa": ["on"],
            },
            capture.extract_fit_flags(replay),
        )

    def test_fit_helper_projection_matches_production_runner_contract(self) -> None:
        common = [
            "-m",
            "model.gguf",
            "--host",
            "127.0.0.1",
            "--port",
            "19150",
            "--parallel",
            "1",
            "--no-warmup",
            "--fit",
            "on",
            "--metrics",
            "-c",
            "4096",
            "-fa",
            "on",
            "-ctk",
            "q8_0",
            "-ctv",
            "q8_0",
        ]
        expected = [
            "-m",
            "model.gguf",
            "--parallel",
            "1",
            "--fit",
            "on",
            "-c",
            "4096",
            "-fa",
            "on",
            "-ctk",
            "q8_0",
            "-ctv",
            "q8_0",
        ]
        role_vectors = (
            ("chat", ["--jinja"]),
            ("embedding", ["--embeddings", "--pooling", "mean"]),
            ("reranker", ["--rerank", "--pooling", "rank"]),
        )

        for role, role_args in role_vectors:
            with self.subTest(role=role):
                explore = [*common, *role_args, "-v"]
                self.assertEqual(expected, capture.project_fit_helper_arguments(explore))

    def test_fit_capture_requires_exact_fit_on_and_matching_kv_flash_policy(self) -> None:
        self.assertEqual(["on"], capture.option_values(["--fit", "on"], "--fit"))
        with self.assertRaisesRegex(capture.CaptureError, "settings differ"):
            capture.validate_kv_flash_equivalence(
                {"-ctk": ["q8_0"], "-ctv": ["q8_0"], "-fa": ["on"]},
                {"-ctk": ["q8_0"], "-ctv": ["q4_0"], "-fa": ["on"]},
            )
        with self.assertRaisesRegex(capture.CaptureError, "matching -ctk/-ctv"):
            capture.validate_kv_flash_equivalence(
                {"-fa": ["on"]},
                {"-fa": ["on"]},
            )

    def test_fit_flags_reject_unresolved_or_partial_placement(self) -> None:
        invalid = (
            ({}, "exactly one -c"),
            ({"-c": ["4096"]}, "exactly one -ngl"),
            ({"-c": ["0"], "-ngl": ["32"]}, "positive concrete integer"),
            ({"-c": ["model"], "-ngl": ["32"]}, "positive concrete integer"),
            ({"-c": ["4096"], "-ngl": ["-1"]}, "automatic placement"),
            ({"-c": ["4096"], "-ngl": ["layers"]}, "integer"),
            ({"-c": ["4096", "8192"], "-ngl": ["32"]}, "exactly one -c"),
            ({"-c": ["4096"], "-ngl": ["32", "16"]}, "exactly one -ngl"),
        )

        for fitted, message in invalid:
            with self.subTest(fitted=fitted), self.assertRaisesRegex(capture.CaptureError, message):
                capture.validate_concrete_fit_flags(fitted)

    def test_fit_flags_accept_concrete_count_and_explicit_all_layers(self) -> None:
        capture.validate_concrete_fit_flags({"-c": ["4096"], "-ngl": ["32"]})
        capture.validate_concrete_fit_flags({"-c": ["4096"], "-ngl": ["-2"]})

    def test_fit_flags_normalize_auto_only_with_authoritative_full_offload(self) -> None:
        fitted = {"-c": ["4096"], "-ngl": ["-1"]}
        verbose = "load_tensors: offloaded 25/25 layers to GPU"

        self.assertEqual(
            {"-c": ["4096"], "-ngl": ["-2"]},
            capture.normalize_fit_flags(fitted, verbose),
        )

        for evidence in (
            "",
            "load_tensors: offloaded 24/25 layers to GPU",
            "load_tensors: offloaded 25/25 layers to GPU\nload_tensors: offloaded 4/10 layers to GPU",
        ):
            with self.subTest(evidence=evidence), self.assertRaisesRegex(capture.CaptureError, "full-offload evidence"):
                capture.normalize_fit_flags(fitted, evidence)

    def test_vram_readers_parse_global_and_process_budget_separately(self) -> None:
        ambient = {"nvidia_smi": {"exit_code": 0, "stdout": "0, GPU, 1.0, 32607, 28496, 3692, 5"}}
        device = {"stdout": "CUDA0: GPU (32606 MiB, 30927 MiB free)", "stderr": ""}
        self.assertEqual(28496, capture.global_gpu_free_mib(ambient))
        self.assertEqual(30927, capture.process_budget_free_mib(device))

    def test_future_gpu_probes_do_not_request_device_uuid(self) -> None:
        commands: list[list[str]] = []

        def capture_command(argv: list[str]) -> dict:
            commands.append(argv)
            return {"argv": argv, "exit_code": 0, "stdout": "", "stderr": ""}

        with mock.patch.object(capture, "capture_text", side_effect=capture_command):
            capture.capture_ambient()
            capture.capture_host(Path("/tmp/llama-server"))  # noqa: S108  # test fixture path, never opened

        gpu_queries = [argument for command in commands for argument in command if argument.startswith("--query-gpu=")]
        self.assertTrue(gpu_queries)
        self.assertTrue(all("uuid" not in query.lower() for query in gpu_queries))

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
                "schema_version": "1.0",
                "kind": "inference-benchmark-capture",
                "capture_id": "baseline-test",
                "phase": "baseline",
                "models": [
                    {"name": "dense", "role": "chat", "quant": "Q4_K_M", "path": str(model), "sha256": digest(model)}
                ],
                "corpus": {"name": "golden", "path": str(corpus), "sha256": digest(corpus)},
                "runtime": {
                    "tag": "b9692",
                    "provenance": "managed-source-build",
                    "backend": "cuda",
                    "path": str(runtime),
                    "sha256": digest(runtime),
                },
                "framework": {
                    "source_commit": "1234567",
                    "maf_version": "1.15.0",
                    "meai_version": "10.8.1",
                    "openai_version": "2.12.0",
                },
                "benchmark": {
                    "cache_state": "cold",
                    "cache_preparation": "delete cache",
                    "ambient_load_policy": "idle",
                    "acceptance_rule": "median/p95",
                },
                "coverage": {"unvalidated": [{"target": "Vulkan", "reason": "No ICD"}]},
                "commands": [
                    {
                        "name": "chat",
                        "argv": ["/bin/sh", "-c", "printf '{\"ttft_ms\": 10}'"],
                        "warmups": 0,
                        "repeats": 2,
                        "timeout_seconds": 5,
                    }
                ],
            }
            spec_path, output = root / "spec.json", root / "output.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")
            capture.capture_baseline(spec_path, output)
            artifact = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("1.15.0", artifact["framework"]["maf_version"])
            self.assertEqual("Vulkan", artifact["coverage"]["unvalidated"][0]["target"])
            self.assertEqual(10, artifact["commands"][0]["aggregates"]["ttft_ms"]["median"])

    def test_framework_command_verifies_historical_worktree_pins_and_assemblies_before_execution(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            repository = root / "historical-worktree"
            repository.mkdir()
            git(repository, "init", "-q")
            git(repository, "config", "user.email", "capture@example.invalid")
            git(repository, "config", "user.name", "Capture Test")
            (repository / ".gitignore").write_text("bin/\n", encoding="utf-8")
            (repository / "Directory.Packages.props").write_text(
                "<Project><ItemGroup>"
                '<PackageVersion Include="Microsoft.Agents.AI" Version="1.15.0" />'
                '<PackageVersion Include="Microsoft.Extensions.AI" Version="10.8.1" />'
                '<PackageVersion Include="OpenAI" Version="2.12.0" />'
                "</ItemGroup></Project>",
                encoding="utf-8",
            )
            git(repository, "add", ".gitignore", "Directory.Packages.props")
            git(repository, "commit", "-qm", "historical baseline")
            marker = root / "executed"
            spec = framework_baseline_spec(root, repository, marker)
            spec_path = root / "spec.json"
            output = root / "output.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")

            capture.capture_baseline(spec_path, output)

            artifact = json.loads(output.read_text(encoding="utf-8"))
            identity = artifact["verified_identity"]["framework"]
            command_tree = identity["command_trees"][0]
            self.assertTrue(marker.exists())
            self.assertTrue(identity["verified"])
            self.assertEqual(spec["framework"]["source_commit"], command_tree["git_head"])
            self.assertTrue(command_tree["git_clean"])
            self.assertTrue(command_tree["central_package_pins"]["sha256_verified"])
            self.assertTrue(command_tree["assemblies"][0]["sha256_verified"])
            self.assertEqual(
                "1.15.0",
                command_tree["resolved_package_versions"]["Microsoft.Agents.AI"],
            )

    def test_framework_command_rejects_declared_commit_drift_before_execution(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            repository = root / "historical-worktree"
            repository.mkdir()
            git(repository, "init", "-q")
            git(repository, "config", "user.email", "capture@example.invalid")
            git(repository, "config", "user.name", "Capture Test")
            (repository / ".gitignore").write_text("bin/\n", encoding="utf-8")
            (repository / "Directory.Packages.props").write_text(
                "<Project><ItemGroup>"
                '<PackageVersion Include="Microsoft.Agents.AI" Version="1.15.0" />'
                '<PackageVersion Include="Microsoft.Extensions.AI" Version="10.8.1" />'
                '<PackageVersion Include="OpenAI" Version="2.12.0" />'
                "</ItemGroup></Project>",
                encoding="utf-8",
            )
            git(repository, "add", ".gitignore", "Directory.Packages.props")
            git(repository, "commit", "-qm", "historical baseline")
            first_commit = git(repository, "rev-parse", "HEAD")
            (repository / "tracked.txt").write_text("next", encoding="utf-8")
            git(repository, "add", "tracked.txt")
            git(repository, "commit", "-qm", "later commit")
            marker = root / "executed"
            spec = framework_baseline_spec(root, repository, marker)
            spec["framework"]["source_commit"] = first_commit
            spec_path = root / "spec.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")

            with self.assertRaisesRegex(capture.CaptureError, "does not match"):
                capture.capture_baseline(spec_path, root / "output.json")

            self.assertFalse(marker.exists())

    def test_framework_command_rejects_dirty_tree_and_package_version_drift_before_execution(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            repository = root / "historical-worktree"
            repository.mkdir()
            git(repository, "init", "-q")
            git(repository, "config", "user.email", "capture@example.invalid")
            git(repository, "config", "user.name", "Capture Test")
            (repository / ".gitignore").write_text("bin/\n", encoding="utf-8")
            pins = repository / "Directory.Packages.props"
            pins.write_text(
                "<Project><ItemGroup>"
                '<PackageVersion Include="Microsoft.Agents.AI" Version="1.15.0" />'
                '<PackageVersion Include="Microsoft.Extensions.AI" Version="10.8.1" />'
                '<PackageVersion Include="OpenAI" Version="2.12.0" />'
                "</ItemGroup></Project>",
                encoding="utf-8",
            )
            git(repository, "add", ".gitignore", "Directory.Packages.props")
            git(repository, "commit", "-qm", "historical baseline")
            marker = root / "executed"
            spec = framework_baseline_spec(root, repository, marker)
            spec_path = root / "spec.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")

            pins.write_text(pins.read_text(encoding="utf-8") + "\n", encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "Git tree is not clean"):
                capture.capture_baseline(spec_path, root / "dirty-output.json")
            self.assertFalse(marker.exists())

            git(repository, "restore", "Directory.Packages.props")
            spec["framework"]["maf_version"] = "9.9.9"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")
            with self.assertRaisesRegex(capture.CaptureError, "Microsoft.Agents.AI is pinned"):
                capture.capture_baseline(spec_path, root / "version-output.json")
            self.assertFalse(marker.exists())

    def test_fit_capture_proves_default_verbose_helper_and_replay_vectors(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            server = root / "llama-server"
            helper = root / "llama-fit-params"
            server.write_text("#!/bin/sh\nprintf 'server output\\n'\n", encoding="utf-8")
            helper.write_text("#!/bin/sh\nprintf '%s\\n' '-c 4096 -ngl 99 -ts 1.0'\n", encoding="utf-8")
            server.chmod(0o755)
            helper.chmod(0o755)
            spec = fit_capture_spec(server, helper)
            spec_path = root / "spec.json"
            output = root / "result.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")
            capture.capture_fit(spec_path, output)
            artifact = json.loads(output.read_text(encoding="utf-8"))
            self.assertTrue(artifact["equivalence"]["placement_equal"])
            self.assertTrue(artifact["equivalence"]["non_fit_vector_equal"])
            self.assertTrue(artifact["equivalence"]["kv_flash_equal"])
            self.assertEqual(
                {"-ctk": ["q8_0"], "-ctv": ["q8_0"], "-fa": ["on"]},
                artifact["equivalence"]["explore_policy_flags"],
            )
            self.assertNotIn("-v", artifact["captures"]["default_verbosity"]["argv"])
            self.assertIn("-v", artifact["captures"]["verbose"]["argv"])
            self.assertEqual(artifact["captures"]["verbose"]["argv"], artifact["captures"]["explore"]["argv"])
            self.assertEqual(artifact["launch_vectors"]["explore"], artifact["captures"]["explore"]["argv"][1:])
            self.assertEqual(artifact["launch_vectors"]["replay"], artifact["captures"]["replay"]["argv"][1:])

    def test_fit_capture_rejects_production_explore_without_exactly_one_verbose_flag(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            server = root / "llama-server"
            helper = root / "llama-fit-params"
            server.write_text("#!/bin/sh\nprintf 'server output\\n'\n", encoding="utf-8")
            helper.write_text("#!/bin/sh\nprintf '%s\\n' '-c 4096 -ngl 99 -ts 1.0'\n", encoding="utf-8")
            server.chmod(0o755)
            helper.chmod(0o755)
            spec = fit_capture_spec(server, helper)
            for name in ("verbose", "explore"):
                spec["commands"][name]["argv"].remove("-v")
            spec["launch_vectors"]["explore"].remove("-v")
            spec_path = root / "spec.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")

            with self.assertRaisesRegex(capture.CaptureError, "exactly one -v/--verbose"):
                capture.capture_fit(spec_path, root / "result.json")

    def test_fit_capture_normalizes_automatic_layers_from_actual_explore_startup_only(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            counter = root / "server-count"
            server = root / "llama-server"
            helper = root / "llama-fit-params"
            server.write_text(
                "#!/usr/bin/env python3\n"
                "from pathlib import Path\n"
                f"counter = Path({str(counter)!r})\n"
                "count = int(counter.read_text()) + 1 if counter.exists() else 1\n"
                "counter.write_text(str(count))\n"
                "if count == 2:\n"
                "    print('load_tensors: offloaded 25/25 layers to GPU')\n"
                "elif count == 3:\n"
                "    print('load_tensors: offloaded 24/25 layers to GPU')\n",
                encoding="utf-8",
            )
            helper.write_text("#!/bin/sh\nprintf '%s\\n' '-c 4096 -ngl -1'\n", encoding="utf-8")
            server.chmod(0o755)
            helper.chmod(0o755)
            spec = fit_capture_spec(server, helper)
            replay = spec["commands"]["replay"]["argv"]
            replay[replay.index("--n-gpu-layers") + 1] = "-2"
            spec["launch_vectors"]["replay"] = replay[1:]
            spec_path = root / "spec.json"
            spec_path.write_text(json.dumps(spec), encoding="utf-8")

            with self.assertRaisesRegex(capture.CaptureError, "full-offload evidence"):
                capture.capture_fit(spec_path, root / "result.json")

    def test_compare_rejects_changed_runtime_identity(self) -> None:
        baseline = {
            "kind": "inference-benchmark-evidence",
            "framework": {
                "source_commit": "1234567",
                "maf_version": "1",
                "meai_version": "1",
                "openai_version": "1",
            },
            "verified_identity": {
                "models": [],
                "corpus": {},
                "runtime": {"tag": "a", "provenance": "source", "backend": "cuda", "sha256": "1"},
                "framework": {
                    "required": False,
                    "verified": True,
                    "declaration": {
                        "source_commit": "1234567",
                        "maf_version": "1",
                        "meai_version": "1",
                        "openai_version": "1",
                    },
                    "command_trees": [],
                },
            },
            "host": {},
            "commands": [],
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

    def test_compare_rejects_framework_declaration_that_diverges_from_verified_identity(self) -> None:
        artifact = {
            "kind": "inference-benchmark-evidence",
            "framework": {
                "source_commit": "1234567",
                "maf_version": "1.15.0",
                "meai_version": "10.8.1",
                "openai_version": "2.12.0",
            },
            "verified_identity": {
                "models": [],
                "corpus": {},
                "runtime": {},
                "framework": {
                    "required": False,
                    "verified": True,
                    "declaration": {
                        "source_commit": "1234567",
                        "maf_version": "1.15.0",
                        "meai_version": "10.8.1",
                        "openai_version": "2.12.0",
                    },
                    "command_trees": [],
                },
            },
            "host": {},
            "commands": [],
        }
        candidate = json.loads(json.dumps(artifact))
        candidate["framework"]["maf_version"] = "9.9.9"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            first, second, output = root / "first.json", root / "second.json", root / "output.json"
            first.write_text(json.dumps(artifact), encoding="utf-8")
            second.write_text(json.dumps(candidate), encoding="utf-8")

            with self.assertRaisesRegex(capture.CaptureError, "declaration differs"):
                capture.compare_artifacts(first, second, output)

    def test_sanitize_replaces_absolute_paths_and_rejects_unmapped_home(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(
                json.dumps({"path": "/home/sam/models/model.gguf", "argv": ["/opt/runtime/llama-server"]}),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(capture.CaptureError, "user-home path"):
                capture.sanitize_artifact(source, output, ["/opt/runtime=$RUNTIME_ROOT"])
            capture.sanitize_artifact(
                source,
                output,
                ["/home/sam/models=$MODEL_ROOT", "/opt/runtime=$RUNTIME_ROOT"],
            )
            sanitized = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("$MODEL_ROOT/model.gguf", sanitized["path"])
            self.assertEqual("$RUNTIME_ROOT/llama-server", sanitized["argv"][0])
            self.assertTrue(sanitized["sanitized"])

    def test_sanitize_redacts_host_cpu_kernel_and_os_but_keeps_gpu_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(
                json.dumps(
                    {
                        "host": {
                            "cpu": "ExampleVendor Model X 16-Core Processor",
                            "kernel": "9.99.99.9-example-standard-WSL2",
                            "os": "Linux-9.99.99.9-example-standard-WSL2-x86_64-with-glibc2.99",
                            "architecture": "x86_64",
                            "nvidia_smi_driver": {
                                "stdout": "0, NVIDIA GeForce RTX 5090, 610.74",
                            },
                        },
                        "run_stdout": (
                            '{"cpu_info": "ExampleVendor Model X 16-Core Processor", '
                            '"gpu_info": "NVIDIA GeForce RTX 5090"}'
                        ),
                    }
                ),
                encoding="utf-8",
            )

            capture.sanitize_artifact(source, output, [])

            sanitized = json.loads(output.read_text(encoding="utf-8"))
            host = sanitized["host"]
            self.assertEqual("<redacted-cpu>", host["cpu"])
            self.assertEqual("<redacted-kernel>", host["kernel"])
            self.assertEqual("Linux-<redacted-kernel>-x86_64", host["os"])
            # Embedded tool output is scrubbed too, not just the host block.
            self.assertNotIn("ExampleVendor", json.dumps(sanitized))
            self.assertNotIn("9.99.99", json.dumps(sanitized))
            # GPU name and driver are benchmark metadata and stay.
            self.assertIn("NVIDIA GeForce RTX 5090", host["nvidia_smi_driver"]["stdout"])
            self.assertIn("610.74", host["nvidia_smi_driver"]["stdout"])
            labels = [entry["label"] for entry in sanitized["sanitization"]["replacements"]]
            self.assertIn("<redacted-cpu>", labels)
            self.assertIn("<redacted-kernel>", labels)

    def test_sanitize_leaves_unknown_host_cpu_and_missing_host_alone(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(
                json.dumps({"host": {"cpu": "unknown", "kernel": ""}, "other": 1}),
                encoding="utf-8",
            )

            capture.sanitize_artifact(source, output, [])

            sanitized = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("unknown", sanitized["host"]["cpu"])
            self.assertEqual("", sanitized["host"]["kernel"])
            self.assertEqual([], sanitized["sanitization"]["replacements"])

    def test_sanitize_redacts_gpu_uuid_and_rejects_identifier_leakage(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(
                json.dumps(
                    {
                        "gpu": "GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5",
                        "mig_legacy": "MIG-GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5/7/3",
                        "mig_current": "MIG-3f3f2b11-0f24-4f10-a2d8-65d7bd9a4c99",
                    }
                ),
                encoding="utf-8",
            )

            capture.sanitize_artifact(source, output, [])

            sanitized = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("<gpu-uuid>", sanitized["gpu"])
            self.assertEqual("<gpu-uuid>", sanitized["mig_legacy"])
            self.assertEqual("<gpu-uuid>", sanitized["mig_current"])
            self.assertNotRegex(json.dumps(sanitized), r"(?:GPU|MIG)-[0-9a-f]")


class GateInferencePolicyTests(unittest.TestCase):
    def run_gate(
        self,
        baseline: dict,
        candidate: dict,
        policy: dict,
    ) -> tuple[int, dict]:
        with tempfile.TemporaryDirectory() as raw:
            paths = write_gate_inputs(Path(raw), baseline, candidate, policy)
            exit_code = capture.gate_artifacts(*paths)
            verdict = json.loads(paths[-1].read_text(encoding="utf-8"))
        return exit_code, verdict

    def run_gate_cli(
        self,
        root: Path,
        baseline: dict,
        candidate: dict,
        policy: dict,
    ) -> tuple[subprocess.CompletedProcess[str], dict]:
        baseline_path, candidate_path, policy_path, output_path = write_gate_inputs(
            root,
            baseline,
            candidate,
            policy,
        )
        completed = subprocess.run(
            [
                os.fspath(Path(os.sys.executable)),
                os.fspath(MODULE_PATH),
                "gate",
                "--baseline",
                os.fspath(baseline_path),
                "--candidate",
                os.fspath(candidate_path),
                "--policy",
                os.fspath(policy_path),
                "--output",
                os.fspath(output_path),
            ],
            text=True,
            capture_output=True,
            check=False,
        )
        return completed, json.loads(output_path.read_text(encoding="utf-8"))

    def test_gate_accepts_equal_identity_with_default_no_allowed_changes(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)

        exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

        self.assertEqual(0, exit_code)
        self.assertEqual("passed", verdict["identity"]["status"])
        self.assertEqual([], verdict["identity"]["allowed_changes"])

    def test_gate_accepts_declared_verified_framework_change(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        candidate["framework"]["source_commit"] = "fedcba9876543210"
        candidate["verified_identity"]["framework"]["declaration"]["source_commit"] = "fedcba9876543210"

        exit_code, verdict = self.run_gate(
            baseline,
            candidate,
            gate_policy(allowed_identity_changes=["framework"]),
        )

        self.assertEqual(0, exit_code)
        self.assertEqual(["framework"], verdict["identity"]["changed_dimensions"])

    def test_gate_accepts_fully_verified_framework_tree_pin_and_assembly_changes(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        add_verified_framework_command_tree(baseline, "a")
        add_verified_framework_command_tree(candidate, "b")

        exit_code, verdict = self.run_gate(
            baseline,
            candidate,
            gate_policy(allowed_identity_changes=["framework"]),
        )

        self.assertEqual(0, exit_code)
        self.assertEqual("passed", verdict["status"])
        self.assertEqual(["framework"], verdict["identity"]["changed_dimensions"])

    def test_gate_accepts_valid_native_only_framework_identity_without_command_trees(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        for artifact in (baseline, candidate):
            artifact["verified_identity"]["framework"].update(
                {
                    "required": False,
                    "verified": True,
                    "declaration": copy.deepcopy(artifact["framework"]),
                    "command_trees": [],
                }
            )

        exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

        self.assertEqual(0, exit_code)
        self.assertEqual("passed", verdict["status"])
        self.assertEqual("identity.matched", verdict["identity"]["reason"])

    def test_gate_rejects_inconsistent_framework_required_and_command_tree_states(self) -> None:
        mutations = {
            "native-only-with-tree": lambda identity: identity.update({"required": False}),
            "framework-required-without-tree": lambda identity: identity.update(
                {
                    "required": True,
                    "command_trees": [],
                }
            ),
        }
        for case, mutation in mutations.items():
            with self.subTest(case=case):
                baseline = gate_artifact(100)
                candidate = gate_artifact(110)
                mutation(candidate["verified_identity"]["framework"])

                exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertEqual("identity.unverified", verdict["identity"]["reason"])
                self.assertTrue(verdict["rules"])
                self.assertTrue(all(rule["passed"] is False for rule in verdict["rules"]))

    def test_gate_accepts_declared_verified_runtime_change(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        candidate["verified_identity"]["runtime"].update(
            {
                "tag": "b9999",
                "sha256": "7" * 64,
                "runtime_local_dependencies": {"manifest_sha256": "8" * 64},
            }
        )

        exit_code, verdict = self.run_gate(
            baseline,
            candidate,
            gate_policy(allowed_identity_changes=["runtime"]),
        )

        self.assertEqual(0, exit_code)
        self.assertEqual(["runtime"], verdict["identity"]["changed_dimensions"])

    def test_gate_rejects_undeclared_framework_and_runtime_identity_changes(self) -> None:
        changes = {
            "framework": lambda artifact: (
                artifact["framework"].update({"source_commit": "fedcba9876543210"}),
                artifact["verified_identity"]["framework"]["declaration"].update({"source_commit": "fedcba9876543210"}),
            ),
            "runtime": lambda artifact: artifact["verified_identity"]["runtime"].update({"tag": "b9999"}),
        }
        for dimension, change in changes.items():
            with self.subTest(dimension=dimension):
                baseline = gate_artifact(100)
                candidate = gate_artifact(110)
                change(candidate)

                exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertEqual("identity.undeclared_mismatch", verdict["identity"]["reason"])

    def test_gate_rejects_unknown_or_duplicate_allowed_identity_changes(self) -> None:
        for allowed in (["models"], ["framework", "framework"]):
            with self.subTest(allowed=allowed):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(110),
                    gate_policy(allowed_identity_changes=allowed),
                )

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertIn("policy.malformed", json.dumps(verdict))

    def test_gate_rejects_unverified_allowed_framework_or_runtime_identity(self) -> None:
        cases = ("framework", "runtime")
        for dimension in cases:
            with self.subTest(dimension=dimension):
                baseline = gate_artifact(100)
                candidate = gate_artifact(110)
                if dimension == "framework":
                    candidate["verified_identity"]["framework"]["verified"] = False
                else:
                    candidate["verified_identity"]["runtime"]["sha256_verified"] = False

                exit_code, verdict = self.run_gate(
                    baseline,
                    candidate,
                    gate_policy(allowed_identity_changes=[dimension]),
                )

                self.assertEqual(2, exit_code)
                self.assertEqual("identity.unverified", verdict["identity"]["reason"])

    def test_gate_rejects_empty_or_incomplete_verified_identities(self) -> None:
        mutations = {
            "empty-identity": lambda artifact: artifact.update({"verified_identity": {}}),
            "empty-models": lambda artifact: artifact["verified_identity"].update({"models": []}),
            "empty-model-digest": lambda artifact: artifact["verified_identity"]["models"][0].update({"sha256": ""}),
            "empty-corpus-digest": lambda artifact: artifact["verified_identity"]["corpus"].update({"sha256": ""}),
            "empty-runtime-digest": lambda artifact: artifact["verified_identity"]["runtime"].update({"sha256": ""}),
            "empty-runtime-dependency-digest": lambda artifact: artifact["verified_identity"]["runtime"][
                "runtime_local_dependencies"
            ].update({"manifest_sha256": ""}),
            "empty-framework-declaration": lambda artifact: (
                artifact.update({"framework": {}}),
                artifact["verified_identity"]["framework"].update({"declaration": {}}),
            ),
            "empty-host": lambda artifact: artifact.update({"host": {}}),
            "empty-devices": lambda artifact: artifact["host"].update({"runtime_devices": {}}),
        }
        for case, mutation in mutations.items():
            with self.subTest(case=case):
                baseline = gate_artifact(100)
                candidate = gate_artifact(110)
                mutation(candidate)

                exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertEqual("identity.unverified", verdict["identity"]["reason"])
                self.assertTrue(verdict["rules"])
                self.assertTrue(all(rule["passed"] is False for rule in verdict["rules"]))

    def test_gate_rejects_malformed_identity_field_types(self) -> None:
        mutations = {
            "models-object": lambda artifact: artifact["verified_identity"].update({"models": {}}),
            "model-string": lambda artifact: artifact["verified_identity"].update({"models": ["model"]}),
            "corpus-string": lambda artifact: artifact["verified_identity"].update({"corpus": "corpus"}),
            "runtime-string": lambda artifact: artifact["verified_identity"].update({"runtime": "runtime"}),
            "framework-string": lambda artifact: artifact["verified_identity"].update({"framework": "framework"}),
            "host-string": lambda artifact: artifact.update({"host": "host"}),
            "devices-string": lambda artifact: artifact["host"].update({"runtime_devices": "devices"}),
        }
        for case, mutation in mutations.items():
            with self.subTest(case=case):
                candidate = gate_artifact(110)
                mutation(candidate)

                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    candidate,
                    gate_policy(),
                )

                self.assertEqual(2, exit_code)
                self.assertEqual("identity.unverified", verdict["identity"]["reason"])
                self.assertTrue(all(rule["passed"] is False for rule in verdict["rules"]))

    def test_gate_reads_median_and_p95_aggregates(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        candidate["commands"][0]["aggregates"]["throughput"]["p95"] = 125
        rules = [
            {
                "id": "median-throughput",
                "command": "chat",
                "metric": "throughput",
                "statistic": "median",
                "kind": "minimum_improvement_percent",
                "threshold_percent": 10,
            },
            {
                "id": "p95-throughput",
                "command": "chat",
                "metric": "throughput",
                "statistic": "p95",
                "kind": "minimum_improvement_percent",
                "threshold_percent": 25,
            },
        ]

        exit_code, verdict = self.run_gate(
            baseline,
            candidate,
            gate_policy(rules=rules),
        )

        self.assertEqual(0, exit_code)
        self.assertEqual([110, 125], [item["candidate_value"] for item in verdict["rules"]])

    def test_gate_minimum_improvement_passes_equality_and_rejects_below_threshold(self) -> None:
        for candidate_value, expected_exit, expected_reason in (
            (110, 0, "rule.passed"),
            (109.999, 3, "rule.threshold_rejected"),
        ):
            with self.subTest(candidate_value=candidate_value):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(candidate_value),
                    gate_policy(threshold=10),
                )

                self.assertEqual(expected_exit, exit_code)
                self.assertEqual(expected_reason, verdict["rules"][0]["reason"])

    def test_gate_maximum_regression_passes_equality_and_rejects_above_threshold(self) -> None:
        for candidate_value, expected_exit, expected_reason in (
            (105, 0, "rule.passed"),
            (105.001, 3, "rule.threshold_rejected"),
        ):
            with self.subTest(candidate_value=candidate_value):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(candidate_value),
                    gate_policy(kind="maximum_regression_percent", threshold=5),
                )

                self.assertEqual(expected_exit, exit_code)
                self.assertEqual(expected_reason, verdict["rules"][0]["reason"])

    def test_gate_rejects_adjacent_float_below_minimum_improvement_boundary(self) -> None:
        exact_candidate = 110.0
        below_candidate = math.nextafter(exact_candidate, -math.inf)

        exact_exit, exact_verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(exact_candidate),
            gate_policy(threshold=10),
        )
        below_exit, below_verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(below_candidate),
            gate_policy(threshold=10),
        )

        self.assertEqual(0, exact_exit)
        self.assertEqual("rule.passed", exact_verdict["rules"][0]["reason"])
        self.assertEqual(3, below_exit)
        self.assertEqual("rule.threshold_rejected", below_verdict["rules"][0]["reason"])

    def test_gate_rejects_adjacent_float_above_maximum_regression_boundary(self) -> None:
        exact_candidate = 105.0
        above_candidate = math.nextafter(exact_candidate, math.inf)

        exact_exit, exact_verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(exact_candidate),
            gate_policy(kind="maximum_regression_percent", threshold=5),
        )
        above_exit, above_verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(above_candidate),
            gate_policy(kind="maximum_regression_percent", threshold=5),
        )

        self.assertEqual(0, exact_exit)
        self.assertEqual("rule.passed", exact_verdict["rules"][0]["reason"])
        self.assertEqual(3, above_exit)
        self.assertEqual("rule.threshold_rejected", above_verdict["rules"][0]["reason"])

    def test_gate_rejects_arbitrary_precision_integer_below_minimum_improvement_boundary(self) -> None:
        baseline_value = 10**100
        exact_candidate = baseline_value * 110 // 100
        below_candidate = exact_candidate - 1

        exact_exit, exact_verdict = self.run_gate(
            gate_artifact(baseline_value),
            gate_artifact(exact_candidate),
            gate_policy(threshold=10),
        )
        below_exit, below_verdict = self.run_gate(
            gate_artifact(baseline_value),
            gate_artifact(below_candidate),
            gate_policy(threshold=10),
        )

        self.assertEqual(0, exact_exit)
        self.assertEqual("rule.passed", exact_verdict["rules"][0]["reason"])
        self.assertEqual(3, below_exit)
        self.assertEqual("rule.threshold_rejected", below_verdict["rules"][0]["reason"])

    def test_gate_rejects_arbitrary_precision_integer_above_maximum_regression_boundary(self) -> None:
        baseline_value = 10**100
        exact_candidate = baseline_value * 105 // 100
        above_candidate = exact_candidate + 1

        exact_exit, exact_verdict = self.run_gate(
            gate_artifact(baseline_value),
            gate_artifact(exact_candidate),
            gate_policy(kind="maximum_regression_percent", threshold=5),
        )
        above_exit, above_verdict = self.run_gate(
            gate_artifact(baseline_value),
            gate_artifact(above_candidate),
            gate_policy(kind="maximum_regression_percent", threshold=5),
        )

        self.assertEqual(0, exact_exit)
        self.assertEqual("rule.passed", exact_verdict["rules"][0]["reason"])
        self.assertEqual(3, above_exit)
        self.assertEqual("rule.threshold_rejected", above_verdict["rules"][0]["reason"])

    def test_gate_marks_missing_command_metric_statistic_or_aggregates_unevaluable(self) -> None:
        mutations = {
            "command": lambda artifact: artifact["commands"][0].update({"name": "other"}),
            "metric": lambda artifact: artifact["commands"][0]["aggregates"].pop("throughput"),
            "statistic": lambda artifact: artifact["commands"][0]["aggregates"]["throughput"].pop("median"),
            "aggregates": lambda artifact: artifact["commands"][0].pop("aggregates"),
        }
        expected_reasons = {
            "command": "rule.command_missing",
            "metric": "rule.metric_missing",
            "statistic": "rule.statistic_missing",
            "aggregates": "rule.metric_missing",
        }
        for case, mutation in mutations.items():
            with self.subTest(case=case):
                candidate = gate_artifact(110)
                mutation(candidate)

                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    candidate,
                    gate_policy(),
                )

                self.assertEqual(2, exit_code)
                self.assertEqual(expected_reasons[case], verdict["rules"][0]["reason"])

    def test_gate_rejects_duplicate_or_invalid_rule_ids_and_unknown_fields_or_enums(self) -> None:
        valid = gate_policy()["rules"][0]
        malformed_rules = {
            "duplicate-id": [valid, copy.deepcopy(valid)],
            "invalid-id": [{**valid, "id": "Not Valid"}],
            "unknown-field": [{**valid, "unexpected": True}],
            "unknown-statistic": [{**valid, "statistic": "average"}],
            "unknown-kind": [{**valid, "kind": "better"}],
        }
        for case, rules in malformed_rules.items():
            with self.subTest(case=case):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(110),
                    gate_policy(rules=rules),
                )

                self.assertEqual(2, exit_code)
                self.assertIn("policy.malformed", json.dumps(verdict))

    def test_gate_rejects_unknown_top_level_policy_fields(self) -> None:
        policy = gate_policy()
        policy["unexpected"] = True

        exit_code, verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(110),
            policy,
        )

        self.assertEqual(2, exit_code)
        self.assertIn("policy.malformed", json.dumps(verdict))

    def test_gate_rejects_path_or_secret_policy_metadata_without_leaking_it(self) -> None:
        secret = "/home/private/fixture-secret-token"  # noqa: S105  # test fixture value
        mutations = {
            "policy_id": lambda policy: policy.update({"policy_id": secret}),
            "command": lambda policy: policy["rules"][0].update({"command": secret}),
            "metric": lambda policy: policy["rules"][0].update({"metric": secret}),
        }
        for field, mutation in mutations.items():
            with self.subTest(field=field):
                policy = gate_policy()
                mutation(policy)

                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(110),
                    policy,
                )
                serialized = json.dumps(verdict)

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertIn("policy.malformed", serialized)
                self.assertNotIn(secret, serialized)

    def test_gate_marks_zero_negative_non_numeric_nan_and_infinite_values_unevaluable(self) -> None:
        invalid_values = (0, -1, "fast", float("nan"), float("inf"), -float("inf"))
        for side in ("baseline", "candidate"):
            for invalid_value in invalid_values:
                with self.subTest(side=side, invalid_value=invalid_value):
                    baseline = gate_artifact(100)
                    candidate = gate_artifact(110)
                    target = baseline if side == "baseline" else candidate
                    target["commands"][0]["aggregates"]["throughput"]["median"] = invalid_value

                    exit_code, verdict = self.run_gate(
                        baseline,
                        candidate,
                        gate_policy(),
                    )

                    self.assertEqual(2, exit_code)
                    self.assertIn(
                        verdict["rules"][0]["reason"],
                        {"rule.value_zero_or_negative", "rule.value_non_finite"},
                    )

    def test_gate_rejects_negative_nan_or_infinite_thresholds_as_malformed_policy(self) -> None:
        for threshold in (-1, float("nan"), float("inf"), -float("inf")):
            with self.subTest(threshold=threshold):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(110),
                    gate_policy(threshold=threshold),
                )

                self.assertEqual(2, exit_code)
                self.assertIn("policy.malformed", json.dumps(verdict))

    def test_gate_unevaluable_rule_takes_precedence_over_threshold_rejection(self) -> None:
        rules = [
            {
                "id": "rejected-throughput",
                "command": "chat",
                "metric": "throughput",
                "statistic": "median",
                "kind": "minimum_improvement_percent",
                "threshold_percent": 20,
            },
            {
                "id": "missing-metric",
                "command": "chat",
                "metric": "not-captured",
                "statistic": "median",
                "kind": "minimum_improvement_percent",
                "threshold_percent": 1,
            },
        ]

        exit_code, verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(110),
            gate_policy(rules=rules),
        )

        self.assertEqual(2, exit_code)
        self.assertEqual("unevaluable", verdict["status"])
        self.assertEqual(
            ["rule.threshold_rejected", "rule.metric_missing"],
            [item["reason"] for item in verdict["rules"]],
        )

    def test_gate_preserves_policy_rule_order_without_silent_skipping(self) -> None:
        rule = gate_policy()["rules"][0]
        rules = [
            {**rule, "id": "third"},
            {**rule, "id": "first"},
            {**rule, "id": "second"},
        ]

        exit_code, verdict = self.run_gate(
            gate_artifact(100),
            gate_artifact(110),
            gate_policy(rules=rules),
        )

        self.assertEqual(0, exit_code)
        self.assertEqual(["third", "first", "second"], [item["id"] for item in verdict["rules"]])

    def test_gate_verdict_hashes_raw_baseline_candidate_and_policy_files(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            paths = write_gate_inputs(root, gate_artifact(100), gate_artifact(110), gate_policy())

            exit_code = capture.gate_artifacts(*paths)
            verdict = json.loads(paths[-1].read_text(encoding="utf-8"))

            self.assertEqual(0, exit_code)
            self.assertEqual(
                {
                    "baseline_sha256": digest(paths[0]),
                    "candidate_sha256": digest(paths[1]),
                    "policy_sha256": digest(paths[2]),
                },
                verdict["hashes"],
            )

    def test_gate_rejects_absent_non_array_or_empty_rules(self) -> None:
        malformed_policies = (
            {"schema_version": "1.0", "policy_id": "missing-rules"},
            {"schema_version": "1.0", "policy_id": "object-rules", "rules": {}},
            {"schema_version": "1.0", "policy_id": "empty-rules", "rules": []},
        )
        for policy in malformed_policies:
            with self.subTest(policy_id=policy["policy_id"]):
                exit_code, verdict = self.run_gate(
                    gate_artifact(100),
                    gate_artifact(110),
                    policy,
                )

                self.assertEqual(2, exit_code)
                self.assertEqual("unevaluable", verdict["status"])
                self.assertIn("policy.malformed", json.dumps(verdict))

    def test_gate_rejects_duplicate_command_names_on_each_artifact_side_before_lookup(self) -> None:
        for side in ("baseline", "candidate"):
            with self.subTest(side=side):
                baseline = gate_artifact(100)
                candidate = gate_artifact(110)
                artifact = baseline if side == "baseline" else candidate
                artifact["commands"].append(copy.deepcopy(artifact["commands"][0]))

                exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                self.assertEqual(2, exit_code)
                self.assertEqual("artifact.duplicate_command_name", verdict["identity"]["reason"])
                self.assertEqual(side, verdict["identity"]["side"])

    def test_gate_rejects_invalid_command_names_on_each_artifact_side(self) -> None:
        for side in ("baseline", "candidate"):
            for invalid_name in ("", "   ", None):
                with self.subTest(side=side, invalid_name=invalid_name):
                    baseline = gate_artifact(100)
                    candidate = gate_artifact(110)
                    artifact = baseline if side == "baseline" else candidate
                    artifact["commands"][0]["name"] = invalid_name

                    exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                    self.assertEqual(2, exit_code)
                    self.assertEqual("artifact.malformed", verdict["identity"]["reason"])
                    self.assertEqual(side, verdict["identity"]["side"])

    def test_gate_rejects_malformed_artifact_schema_or_kind(self) -> None:
        mutations = {
            "schema": lambda artifact: artifact.update({"schema_version": "2.0"}),
            "kind": lambda artifact: artifact.update({"kind": "other-evidence"}),
        }
        for side in ("baseline", "candidate"):
            for case, mutation in mutations.items():
                with self.subTest(side=side, case=case):
                    baseline = gate_artifact(100)
                    candidate = gate_artifact(110)
                    mutation(baseline if side == "baseline" else candidate)

                    exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

                    self.assertEqual(2, exit_code)
                    self.assertEqual("artifact.malformed", verdict["identity"]["reason"])

    def test_gate_rejects_changed_command_argv_hash(self) -> None:
        candidate = gate_artifact(110)
        candidate["commands"][0]["argv_sha256"] = "9" * 64

        exit_code, verdict = self.run_gate(
            gate_artifact(100),
            candidate,
            gate_policy(),
        )

        self.assertEqual(2, exit_code)
        self.assertEqual("identity.undeclared_mismatch", verdict["rules"][0]["reason"])

    def test_gate_unreferenced_extra_command_name_mismatch_makes_every_rule_unevaluable(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        baseline["commands"].append(
            {
                "name": "baseline-diagnostic",
                "argv_sha256": "a" * 64,
                "aggregates": {"diagnostic": {"median": 1, "p95": 1}},
            }
        )
        candidate["commands"].append(
            {
                "name": "candidate-diagnostic",
                "argv_sha256": "a" * 64,
                "aggregates": {"diagnostic": {"median": 1, "p95": 1}},
            }
        )

        exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

        self.assertEqual(2, exit_code)
        self.assertEqual("identity.undeclared_mismatch", verdict["identity"]["reason"])
        self.assertEqual(["command_names"], verdict["identity"]["changed_dimensions"])
        self.assertTrue(verdict["rules"])
        self.assertTrue(all(rule["passed"] is False for rule in verdict["rules"]))
        self.assertTrue(all(rule["reason"] == "identity.undeclared_mismatch" for rule in verdict["rules"]))

    def test_gate_unreferenced_command_argv_mismatch_makes_every_rule_unevaluable(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        common_command = {
            "name": "diagnostic",
            "argv_sha256": "a" * 64,
            "aggregates": {"diagnostic": {"median": 1, "p95": 1}},
        }
        baseline["commands"].append(copy.deepcopy(common_command))
        candidate["commands"].append(copy.deepcopy(common_command))
        candidate["commands"][1]["argv_sha256"] = "b" * 64

        exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())

        self.assertEqual(2, exit_code)
        self.assertEqual("identity.undeclared_mismatch", verdict["identity"]["reason"])
        self.assertEqual(["command_argv"], verdict["identity"]["changed_dimensions"])
        self.assertTrue(verdict["rules"])
        self.assertTrue(all(rule["passed"] is False for rule in verdict["rules"]))
        self.assertTrue(all(rule["reason"] == "identity.undeclared_mismatch" for rule in verdict["rules"]))

    def test_gate_atomically_replaces_verdict(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            paths = write_gate_inputs(Path(raw), gate_artifact(100), gate_artifact(110), gate_policy())
            paths[-1].write_text("old-verdict", encoding="utf-8")

            with (
                mock.patch.object(capture.os, "replace", wraps=os.replace) as replace,
                mock.patch.object(capture.os, "fsync", wraps=os.fsync) as fsync,
            ):
                exit_code = capture.gate_artifacts(*paths)

            self.assertEqual(0, exit_code)
            self.assertEqual(1, replace.call_count)
            self.assertGreaterEqual(fsync.call_count, 1)
            self.assertEqual("passed", json.loads(paths[-1].read_text(encoding="utf-8"))["status"])

    def test_gate_write_failure_preserves_existing_verdict_and_cleans_temporary_file(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            paths = write_gate_inputs(root, gate_artifact(100), gate_artifact(110), gate_policy())
            existing = b'{"old":true}\n'
            paths[-1].write_bytes(existing)

            with (
                mock.patch.object(capture.os, "replace", side_effect=OSError("fixture-secret")),
                self.assertRaises(capture.CaptureError) as raised,
            ):
                capture.gate_artifacts(*paths)

            self.assertEqual(existing, paths[-1].read_bytes())
            self.assertNotIn("fixture-secret", str(raised.exception))
            self.assertEqual(
                {"baseline.json", "candidate.json", "policy.json", "verdict.json"},
                {item.name for item in root.iterdir()},
            )

    def test_gate_verdict_omits_sensitive_paths_commands_streams_environment_gpu_uuid_and_assemblies(self) -> None:
        baseline = gate_artifact(100)
        candidate = gate_artifact(110)
        gpu_uuid = "GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5"
        baseline["host"]["runtime_devices"]["stdout"] = gpu_uuid
        candidate["host"]["runtime_devices"]["stdout"] = gpu_uuid

        exit_code, verdict = self.run_gate(baseline, candidate, gate_policy())
        serialized = json.dumps(verdict)

        self.assertEqual(0, exit_code)
        for secret in (
            "/sensitive/runtime",
            "/home/private",
            "fixture-secret",
            "TOKEN",
            '"env"',
            gpu_uuid,
            "Framework.Tests.dll",
            "stdout",
            "stderr",
            "environment",
            "argv",
        ):
            with self.subTest(secret=secret):
                self.assertNotIn(secret, serialized)

    def test_policy_schema_and_example_are_valid_and_throughput_only(self) -> None:
        schema = json.loads(POLICY_SCHEMA_PATH.read_text(encoding="utf-8"))
        example = json.loads(POLICY_EXAMPLE_PATH.read_text(encoding="utf-8"))

        self.assertEqual("object", schema["type"])
        self.assertFalse(schema["additionalProperties"])
        self.assertGreaterEqual(len(example["rules"]), 1)
        self.assertTrue(all(rule["metric"] for rule in example["rules"]))
        self.assertNotRegex(json.dumps(example).lower(), r"\b(?:ram|rss|vram|gpu[_ -]?memory)\b")

    def test_policy_schema_requires_exactly_one_minimum_rule(self) -> None:
        schema = json.loads(POLICY_SCHEMA_PATH.read_text(encoding="utf-8"))

        self.assertEqual(1, schema["properties"]["rules"]["minItems"])

    def test_policy_schema_restricts_policy_id_command_and_metric_to_safe_tokens(self) -> None:
        schema = json.loads(POLICY_SCHEMA_PATH.read_text(encoding="utf-8"))
        patterns = {
            "policy_id": schema["properties"]["policy_id"].get("pattern"),
            "command": schema["$defs"]["rule"]["properties"]["command"].get("pattern"),
            "metric": schema["$defs"]["rule"]["properties"]["metric"].get("pattern"),
        }

        self.assertTrue(all(isinstance(pattern, str) and pattern for pattern in patterns.values()))
        for field, pattern in patterns.items():
            with self.subTest(field=field):
                self.assertIsNone(re.fullmatch(pattern, "/home/private/fixture-secret-token"))
                self.assertIsNotNone(re.fullmatch(pattern, "safe-token_1"))

    def test_example_policy_is_accepted_by_evaluator(self) -> None:
        policy = json.loads(POLICY_EXAMPLE_PATH.read_text(encoding="utf-8"))
        baseline = gate_artifact(100)
        candidate = gate_artifact(120)
        baseline_commands: list[dict[str, Any]] = [
            {
                "name": "chat-throughput",
                "argv_sha256": "7" * 64,
                "aggregates": {"tokens_per_second": {"median": 100, "p95": 100}},
            },
            {
                "name": "embedding-throughput",
                "argv_sha256": "8" * 64,
                "aggregates": {"embeddings_per_second": {"median": 100, "p95": 100}},
            },
        ]
        baseline["commands"] = baseline_commands
        candidate_commands: list[dict[str, Any]] = copy.deepcopy(baseline_commands)
        candidate["commands"] = candidate_commands
        candidate_commands[0]["aggregates"]["tokens_per_second"]["median"] = 120
        candidate_commands[1]["aggregates"]["embeddings_per_second"]["median"] = 120

        exit_code, verdict = self.run_gate(baseline, candidate, policy)

        self.assertEqual(0, exit_code)
        self.assertEqual(
            ["chat-throughput-median", "embedding-throughput-median"],
            [rule["id"] for rule in verdict["rules"]],
        )

    def test_gate_cli_returns_zero_with_passing_verdict(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            completed, verdict = self.run_gate_cli(
                Path(raw),
                gate_artifact(100),
                gate_artifact(110),
                gate_policy(),
            )

        self.assertEqual(0, completed.returncode)
        self.assertEqual("passed", verdict["status"])
        self.assertTrue(verdict["passed"])

    def test_gate_cli_returns_two_with_unevaluable_verdict(self) -> None:
        candidate = gate_artifact(110)
        candidate["commands"][0]["aggregates"].pop("throughput")
        with tempfile.TemporaryDirectory() as raw:
            completed, verdict = self.run_gate_cli(
                Path(raw),
                gate_artifact(100),
                candidate,
                gate_policy(),
            )

        self.assertEqual(2, completed.returncode)
        self.assertEqual("unevaluable", verdict["status"])
        self.assertFalse(verdict["passed"])

    def test_gate_cli_returns_three_with_rejected_verdict(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            completed, verdict = self.run_gate_cli(
                Path(raw),
                gate_artifact(100),
                gate_artifact(109),
                gate_policy(),
            )

        self.assertEqual(3, completed.returncode)
        self.assertEqual("rejected", verdict["status"])
        self.assertFalse(verdict["passed"])

    def test_gate_cli_stderr_does_not_leak_secret_fixture_content_or_sensitive_absolute_paths(self) -> None:
        with tempfile.TemporaryDirectory(prefix="sensitive-gate-path-") as raw:
            root = Path(raw)
            baseline_path, candidate_path, policy_path, output_path = write_gate_inputs(
                root,
                gate_artifact(100),
                gate_artifact(110),
                gate_policy(),
            )
            policy_path.write_text('{"fixture-secret-policy":', encoding="utf-8")

            completed = subprocess.run(
                [
                    os.fspath(Path(os.sys.executable)),
                    os.fspath(MODULE_PATH),
                    "gate",
                    "--baseline",
                    os.fspath(baseline_path),
                    "--candidate",
                    os.fspath(candidate_path),
                    "--policy",
                    os.fspath(policy_path),
                    "--output",
                    os.fspath(output_path),
                ],
                text=True,
                capture_output=True,
                check=False,
            )

            verdict = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(2, completed.returncode)
            self.assertEqual("unevaluable", verdict["status"])
            self.assertNotIn("fixture-secret-policy", completed.stderr)
            self.assertNotIn(os.fspath(root), completed.stderr)

    def test_gate_cli_output_io_failure_returns_two_preserves_old_verdict_and_sanitizes_stderr(self) -> None:
        if os.name != "posix":
            self.skipTest("directory-permission output failure requires POSIX permissions")
        with tempfile.TemporaryDirectory(prefix="sensitive-output-path-") as raw:
            root = Path(raw)
            baseline_path, candidate_path, policy_path, output_path = write_gate_inputs(
                root,
                gate_artifact(100),
                gate_artifact(110),
                gate_policy(),
            )
            old_verdict = b'{"old":"fixture-secret-old-verdict"}\n'
            output_path.write_bytes(old_verdict)
            root.chmod(0o500)
            try:
                completed = subprocess.run(
                    [
                        os.fspath(Path(os.sys.executable)),
                        os.fspath(MODULE_PATH),
                        "gate",
                        "--baseline",
                        os.fspath(baseline_path),
                        "--candidate",
                        os.fspath(candidate_path),
                        "--policy",
                        os.fspath(policy_path),
                        "--output",
                        os.fspath(output_path),
                    ],
                    text=True,
                    capture_output=True,
                    check=False,
                )
            finally:
                root.chmod(0o700)

            self.assertEqual(2, completed.returncode)
            self.assertEqual(old_verdict, output_path.read_bytes())
            self.assertNotIn("fixture-secret-old-verdict", completed.stderr)
            self.assertNotIn(os.fspath(root), completed.stderr)
            self.assertEqual(
                {"baseline.json", "candidate.json", "policy.json", "verdict.json"},
                {item.name for item in root.iterdir()},
            )

    def test_compare_report_shape_remains_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            baseline_path, candidate_path, _, output_path = write_gate_inputs(
                root,
                gate_artifact(100),
                gate_artifact(110),
                gate_policy(),
            )

            capture.compare_artifacts(baseline_path, candidate_path, output_path)
            comparison = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual("inference-benchmark-comparison", comparison["kind"])
        self.assertEqual(str(baseline_path.resolve()), comparison["baseline"])
        self.assertEqual(str(candidate_path.resolve()), comparison["candidate"])
        self.assertEqual(10, comparison["commands"]["chat"]["throughput"]["delta_percent"])

    def test_compare_report_preserves_complete_legacy_top_level_and_metric_shape(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            baseline_path, candidate_path, _, output_path = write_gate_inputs(
                root,
                gate_artifact(100),
                gate_artifact(110),
                gate_policy(),
            )

            capture.compare_artifacts(baseline_path, candidate_path, output_path)
            comparison = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual(
            {
                "schema_version",
                "kind",
                "baseline",
                "candidate",
                "identity_equal",
                "framework_identity_equal",
                "framework_identity",
                "commands",
                "generated_at_utc",
            },
            set(comparison),
        )
        self.assertEqual("1.0", comparison["schema_version"])
        self.assertTrue(comparison["identity_equal"])
        self.assertEqual({"baseline", "candidate"}, set(comparison["framework_identity"]))
        self.assertEqual(
            {"baseline_median", "candidate_median", "delta_percent"},
            set(comparison["commands"]["chat"]["throughput"]),
        )


class CaptureInferenceEvidenceCompatibilityTests(unittest.TestCase):
    ORIGINAL_DOCSTRING = """Capture reproducible local-inference benchmark and fit/replay evidence.

The tool intentionally uses only the Python standard library. It treats the input
specification as an immutable experiment contract, verifies every declared file
hash before executing anything, records exact argv vectors, and writes one stable
JSON artifact that can later be compared without reconstructing operator state.
"""
    EXPECTED_EXPORTS = (
        "SCHEMA_VERSION",
        "MAX_CAPTURE_STREAM_BYTES",
        "PROCESS_CLEANUP_TIMEOUT_SECONDS",
        "UUID_CORE_PATTERN",
        "GPU_UUID_PATTERN",
        "FIT_FLAGS_WITH_VALUE",
        "FIT_FLAG_CANONICAL",
        "FIT_HELPER_ARGS_WITH_VALUE",
        "FIT_HELPER_VALUELESS_ARGS",
        "FRAMEWORK_COMMAND_PARTITIONS",
        "FRAMEWORK_PACKAGE_NAMES",
        "POLICY_SCHEMA_VERSION",
        "POLICY_FIELDS",
        "POLICY_REQUIRED_FIELDS",
        "POLICY_RULE_FIELDS",
        "POLICY_SAFE_TOKEN_PATTERN",
        "POLICY_SAFE_TOKEN_MAX_LENGTH",
        "ALLOWED_IDENTITY_CHANGES",
        "POLICY_STATISTICS",
        "POLICY_RULE_KINDS",
        "CaptureError",
        "utc_now",
        "load_json",
        "write_json",
        "write_json_atomic",
        "sha256_file",
        "sha256_tree",
        "require_string",
        "is_finite_number",
        "is_sha256",
        "is_safe_policy_token",
        "verify_identity",
        "runtime_local_dependencies",
        "capture_text",
        "git_text",
        "central_package_versions",
        "is_relative_to",
        "verify_framework_identity",
        "capture_ambient",
        "global_gpu_used_mib",
        "global_gpu_free_mib",
        "process_budget_free_mib",
        "capture_host",
        "validate_spec",
        "command_argv",
        "numeric_metrics",
        "percentile95",
        "BoundedStreamCapture",
        "drain_capture_stream",
        "cleanup_process_group",
        "run_once",
        "signal_process_group",
        "run_command",
        "capture_baseline",
        "strip_one_verbose",
        "extract_fit_flags",
        "project_fit_helper_arguments",
        "option_values",
        "validate_kv_flash_equivalence",
        "validate_concrete_fit_flags",
        "normalize_fit_flags",
        "without_fit_semantics",
        "capture_fit",
        "identity_projection",
        "framework_identity_projection",
        "verified_runtime_identity_projection",
        "verified_framework_identity_projection",
        "immutable_gate_identity_projection",
        "artifact_commands",
        "validate_policy",
        "gate_identity",
        "unevaluable_rule",
        "evaluate_policy_rule",
        "gate_artifacts",
        "compare_artifacts",
        "sanitize_artifact",
        "build_parser",
        "main",
    )
    EXPECTED_CALLABLE_SIGNATURES = {
        "utc_now": "() -> 'str'",
        "load_json": "(path: 'Path') -> 'dict[str, Any]'",
        "write_json": "(path: 'Path', value: 'Any') -> 'None'",
        "write_json_atomic": "(path: 'Path', value: 'Any') -> 'None'",
        "sha256_file": "(path: 'Path') -> 'str'",
        "sha256_tree": "(path: 'Path') -> 'str'",
        "require_string": "(obj: 'dict[str, Any]', key: 'str', context: 'str') -> 'str'",
        "is_finite_number": "(value: 'Any') -> 'TypeGuard[int | float]'",
        "is_sha256": "(value: 'Any') -> 'bool'",
        "is_safe_policy_token": "(value: 'Any') -> 'TypeGuard[str]'",
        "verify_identity": "(label: 'str', entry: 'dict[str, Any]') -> 'dict[str, Any]'",
        "runtime_local_dependencies": "(binary: 'Path') -> 'dict[str, Any]'",
        "capture_text": "(argv: 'list[str]', timeout_seconds: 'float' = 10) -> 'dict[str, Any]'",
        "git_text": "(cwd: 'Path', arguments: 'list[str]', context: 'str') -> 'str'",
        "central_package_versions": "(path: 'Path') -> 'dict[str, str]'",
        "is_relative_to": "(path: 'Path', parent: 'Path') -> 'bool'",
        "verify_framework_identity": (
            "(framework: 'dict[str, Any]', commands: 'list[dict[str, Any]]') -> 'dict[str, Any]'"
        ),
        "capture_ambient": "() -> 'dict[str, Any]'",
        "global_gpu_used_mib": "(ambient: 'dict[str, Any] | None') -> 'int | None'",
        "global_gpu_free_mib": "(ambient: 'dict[str, Any] | None') -> 'int | None'",
        "process_budget_free_mib": "(device_probe: 'dict[str, Any]') -> 'int | None'",
        "capture_host": "(runtime_binary: 'Path') -> 'dict[str, Any]'",
        "validate_spec": "(spec: 'dict[str, Any]', expected_kind: 'str') -> 'None'",
        "command_argv": "(command: 'dict[str, Any]', context: 'str') -> 'list[str]'",
        "numeric_metrics": "(stdout: 'str') -> 'dict[str, float]'",
        "percentile95": "(values: 'list[float]') -> 'float'",
        "BoundedStreamCapture": "() -> 'None'",
        "drain_capture_stream": (
            "(stream: 'Any', capture: 'BoundedStreamCapture', errors: 'list[BaseException]') -> 'None'"
        ),
        "cleanup_process_group": (
            "(process: 'subprocess.Popen[str]', readers: 'tuple[threading.Thread, threading.Thread]', "
            "executable: 'str') -> 'None'"
        ),
        "run_once": "(argv: 'list[str]', timeout_seconds: 'float', expected_timeout: 'bool', cwd: 'str | None', env: "
        "'dict[str, str]') -> 'dict[str, Any]'",
        "signal_process_group": "(process: 'subprocess.Popen[str]', requested_signal: 'signal.Signals') -> 'None'",
        "run_command": "(command: 'dict[str, Any]', context: 'str') -> 'dict[str, Any]'",
        "capture_baseline": "(spec_path: 'Path', output_path: 'Path') -> 'None'",
        "strip_one_verbose": "(argv: 'list[str]') -> 'list[str]'",
        "extract_fit_flags": "(argv: 'list[str]') -> 'dict[str, list[str]]'",
        "project_fit_helper_arguments": "(server_argv: 'list[str]') -> 'list[str]'",
        "option_values": "(argv: 'list[str]', option: 'str') -> 'list[str]'",
        "validate_kv_flash_equivalence": (
            "(explore_flags: 'dict[str, list[str]]', replay_flags: 'dict[str, list[str]]') -> 'None'"
        ),
        "validate_concrete_fit_flags": "(fitted_flags: 'dict[str, list[str]]') -> 'None'",
        "normalize_fit_flags": (
            "(fitted_flags: 'dict[str, list[str]]', verbose_startup: 'str') -> 'dict[str, list[str]]'"
        ),
        "without_fit_semantics": "(argv: 'list[str]') -> 'list[str]'",
        "capture_fit": "(spec_path: 'Path', output_path: 'Path') -> 'None'",
        "identity_projection": "(artifact: 'dict[str, Any]') -> 'dict[str, Any]'",
        "framework_identity_projection": "(artifact: 'dict[str, Any]') -> 'dict[str, Any]'",
        "verified_runtime_identity_projection": "(artifact: 'dict[str, Any]') -> 'dict[str, Any]'",
        "verified_framework_identity_projection": "(artifact: 'dict[str, Any]') -> 'dict[str, Any]'",
        "immutable_gate_identity_projection": "(artifact: 'dict[str, Any]') -> 'dict[str, Any]'",
        "artifact_commands": (
            "(artifact: 'dict[str, Any]', side: 'str') -> 'tuple[list[dict[str, Any]] | None, dict[str, Any] | None]'"
        ),
        "validate_policy": (
            "(policy: 'dict[str, Any]') -> 'tuple[str | None, list[str] | None, list[dict[str, Any]] | None]'"
        ),
        "gate_identity": "(baseline: 'dict[str, Any]', candidate: 'dict[str, Any]', allowed_changes: 'list[str]') -> "
        "'dict[str, Any]'",
        "unevaluable_rule": "(rule: 'dict[str, Any]', reason: 'str') -> 'dict[str, Any]'",
        "evaluate_policy_rule": (
            "(rule: 'dict[str, Any]', baseline_commands: 'dict[str, dict[str, Any]]', "
            "candidate_commands: 'dict[str, dict[str, Any]]') -> 'dict[str, Any]'"
        ),
        "gate_artifacts": (
            "(baseline_path: 'Path', candidate_path: 'Path', policy_path: 'Path', output_path: 'Path') -> 'int'"
        ),
        "compare_artifacts": "(baseline_path: 'Path', candidate_path: 'Path', output_path: 'Path') -> 'None'",
        "sanitize_artifact": "(input_path: 'Path', output_path: 'Path', replacements: 'list[str]') -> 'None'",
        "build_parser": "() -> 'argparse.ArgumentParser'",
        "main": "() -> 'int'",
    }

    def test_facade_preserves_established_exports_and_signatures(self) -> None:
        self.assertEqual(self.ORIGINAL_DOCSTRING, capture.__doc__)
        self.assertEqual(self.EXPECTED_EXPORTS, tuple(capture.__all__))
        self.assertTrue(set(self.EXPECTED_EXPORTS).issubset(vars(capture)))
        actual_signatures = {
            name: str(inspect.signature(getattr(capture, name))) for name in self.EXPECTED_CALLABLE_SIGNATURES
        }
        self.assertEqual(self.EXPECTED_CALLABLE_SIGNATURES, actual_signatures)

    def test_facade_remains_executable_from_an_unrelated_working_directory(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), os.fspath(MODULE_PATH), "--help"],
                cwd=raw,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("baseline", completed.stdout)
        self.assertIn("sanitize", completed.stdout)

    def test_arbitrary_name_dynamic_loader_preserves_exports_without_preconfigured_path(self) -> None:
        loader = """
import importlib.util
import json
import os
import pathlib
import sys

module_path = pathlib.Path(os.environ["CAPTURE_MODULE"])
spec = importlib.util.spec_from_file_location("arbitrary_capture_loader_name", module_path)
assert spec is not None
assert spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
print(json.dumps({
    "error": module.CaptureError.__name__,
    "signature": str(__import__("inspect").signature(module.capture_baseline)),
    "main": callable(module.main),
}))
"""
        with tempfile.TemporaryDirectory() as raw:
            environment = os.environ.copy()
            environment["CAPTURE_MODULE"] = os.fspath(MODULE_PATH)
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), "-c", loader],
                cwd=raw,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual("CaptureError", result["error"])
        self.assertEqual("(spec_path: 'Path', output_path: 'Path') -> 'None'", result["signature"])
        self.assertTrue(result["main"])

    def test_qualified_import_uses_the_relative_package_identity(self) -> None:
        qualified_import = """
import json
import scripts.performance.capture_inference_evidence as facade

print(json.dumps({
    "implementation": facade._implementation.__name__,
    "capture_module": facade.capture_baseline.__module__,
}))
"""
        environment = os.environ.copy()
        environment["PYTHONPATH"] = os.fspath(REPOSITORY_ROOT)
        with tempfile.TemporaryDirectory() as raw:
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), "-c", qualified_import],
                cwd=raw,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual("scripts.performance.inference_evidence", result["implementation"])
        self.assertEqual("scripts.performance.inference_evidence.capture", result["capture_module"])

    def test_arbitrary_loader_ignores_foreign_top_level_package_without_mutating_sys_path(self) -> None:
        isolated_loader = """
import importlib.util
import json
import os
import pathlib
import sys
import types

foreign = types.ModuleType("inference_evidence")
foreign.marker = "foreign"
sys.modules["inference_evidence"] = foreign
sys.modules["unrelated"] = types.ModuleType("unrelated")
before = list(sys.path)
module_path = pathlib.Path(os.environ["CAPTURE_MODULE"])
spec = importlib.util.spec_from_file_location("unrelated.arbitrary_collision_test", module_path)
assert spec is not None
assert spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
print(json.dumps({
    "foreign_preserved": sys.modules["inference_evidence"].marker,
    "implementation": module._implementation.__name__,
    "implementation_file": module._implementation.__file__,
    "sys_path_unchanged": before == sys.path,
}))
"""
        environment = os.environ.copy()
        environment["CAPTURE_MODULE"] = os.fspath(MODULE_PATH)
        with tempfile.TemporaryDirectory() as raw:
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), "-c", isolated_loader],
                cwd=raw,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual("foreign", result["foreign_preserved"])
        self.assertRegex(result["implementation"], r"^_xe_capture_inference_evidence_[0-9a-f]{64}$")
        self.assertEqual(PACKAGE_PATH / "__init__.py", Path(result["implementation_file"]))
        self.assertTrue(result["sys_path_unchanged"])

    def test_two_worktree_copies_load_distinct_exact_sibling_packages_in_one_interpreter(self) -> None:
        loader = """
import importlib.util
import json
import os
import pathlib
import sys
import types

foreign = types.ModuleType("inference_evidence")
foreign.marker = "foreign"
sys.modules["inference_evidence"] = foreign
before = list(sys.path)
modules = []
for index, raw_path in enumerate(json.loads(os.environ["CAPTURE_MODULES"])):
    path = pathlib.Path(raw_path)
    spec = importlib.util.spec_from_file_location(f"worktree_copy_{index}", path)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    modules.append(module)
print(json.dumps({
    "foreign_preserved": sys.modules["inference_evidence"].marker,
    "implementation_names": [module._implementation.__name__ for module in modules],
    "implementation_files": [module._implementation.__file__ for module in modules],
    "capture_modules": [module.capture_baseline.__module__ for module in modules],
    "errors_are_distinct": modules[0].CaptureError is not modules[1].CaptureError,
    "sys_path_unchanged": before == sys.path,
}))
"""
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            module_paths = []
            expected_package_files = []
            for name in ("first", "second"):
                performance = root / name / "scripts" / "performance"
                performance.mkdir(parents=True)
                shutil.copy2(MODULE_PATH, performance / MODULE_PATH.name)
                shutil.copytree(PACKAGE_PATH, performance / PACKAGE_PATH.name)
                module_paths.append(performance / MODULE_PATH.name)
                expected_package_files.append(performance / PACKAGE_PATH.name / "__init__.py")
            environment = os.environ.copy()
            environment["CAPTURE_MODULES"] = json.dumps([os.fspath(path) for path in module_paths])
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), "-c", loader],
                cwd=root,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual("foreign", result["foreign_preserved"])
        self.assertEqual(2, len(set(result["implementation_names"])))
        self.assertEqual(expected_package_files, [Path(path) for path in result["implementation_files"]])
        self.assertEqual(2, len(set(result["capture_modules"])))
        self.assertTrue(result["errors_are_distinct"])
        self.assertTrue(result["sys_path_unchanged"])

    def test_implementation_modules_import_directly_without_loading_the_facade(self) -> None:
        direct_import = """
import json
import sys

from inference_evidence import capture, contracts, identity, policy, process, sanitize

print(json.dumps({
    "facade_loaded": "capture_inference_evidence" in sys.modules,
    "capture": callable(capture.capture_baseline),
    "contracts": contracts.SCHEMA_VERSION,
    "identity": callable(identity.verify_identity),
    "policy": callable(policy.gate_artifacts),
    "process": callable(process.run_once),
    "sanitize": callable(sanitize.sanitize_artifact),
}))
"""
        environment = os.environ.copy()
        environment["PYTHONPATH"] = os.fspath(MODULE_PATH.parent)
        with tempfile.TemporaryDirectory() as raw:
            completed = subprocess.run(
                [os.fspath(Path(os.sys.executable)), "-c", direct_import],
                cwd=raw,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )

        self.assertEqual(0, completed.returncode, completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(
            {
                "facade_loaded": False,
                "capture": True,
                "contracts": "1.0",
                "identity": True,
                "policy": True,
                "process": True,
                "sanitize": True,
            },
            result,
        )

    def test_implementation_dependency_graph_is_acyclic_and_only_points_downward(self) -> None:
        allowed_dependencies = {
            "contracts": set(),
            "process": {"contracts"},
            "identity": {"contracts", "process"},
            "capture": {"contracts", "process", "identity"},
            "policy": {"contracts", "process", "identity"},
            "sanitize": {"contracts", "process", "identity"},
        }
        dependencies: dict[str, set[str]] = {}
        for module_name, allowed in allowed_dependencies.items():
            module_path = PACKAGE_PATH / f"{module_name}.py"
            tree = ast.parse(module_path.read_text(encoding="utf-8"), filename=os.fspath(module_path))
            imported = {
                node.module
                for node in ast.walk(tree)
                if isinstance(node, ast.ImportFrom) and node.level == 1 and node.module is not None
            }
            dependencies[module_name] = imported
            self.assertTrue(imported.issubset(allowed), f"{module_name} imports upward: {imported - allowed}")
            self.assertNotIn("capture_inference_evidence", module_path.read_text(encoding="utf-8"))

        visiting: set[str] = set()
        visited: set[str] = set()

        def visit(module_name: str) -> None:
            self.assertNotIn(module_name, visiting, f"dependency cycle reaches {module_name}")
            if module_name in visited:
                return
            visiting.add(module_name)
            for dependency in dependencies[module_name]:
                visit(dependency)
            visiting.remove(module_name)
            visited.add(module_name)

        for module_name in dependencies:
            visit(module_name)
        self.assertEqual(set(dependencies), visited)


if __name__ == "__main__":
    unittest.main()
