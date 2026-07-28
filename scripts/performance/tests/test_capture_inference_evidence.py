#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import inspect
import json
import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock


MODULE_PATH = Path(__file__).parents[1] / "capture_inference_evidence.py"
SPEC = importlib.util.spec_from_file_location("capture_inference_evidence", MODULE_PATH)
assert SPEC and SPEC.loader
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
                "framework_assemblies": [
                    {"name": assembly.name, "path": str(assembly), "sha256": digest(assembly)}
                ],
                "warmups": 0,
                "repeats": 1,
                "timeout_seconds": 5,
            }
        ],
    }


def fit_capture_spec(server: Path, helper: Path) -> dict:
    base = [
        str(server),
        "-m", "model.gguf",
        "--host", "127.0.0.1",
        "--port", "19150",
        "--parallel", "1",
        "--no-warmup",
    ]
    explore = [
        *base,
        "--fit", "on",
        "--metrics",
        "-c", "4096",
        "-fa", "on",
        "-ctk", "q8_0",
        "-ctv", "q8_0",
        "--jinja",
        "-v",
    ]
    replay = [
        *base,
        "-c", "4096",
        "--n-gpu-layers", "99",
        "-ts", "1.0",
        "-ctk", "q8_0",
        "-ctv", "q8_0",
        "--flash-attn", "on",
        "--jinja",
        "--metrics",
    ]
    helper_argv = [
        str(helper),
        "-m", "model.gguf",
        "--parallel", "1",
        "--fit", "on",
        "-c", "4096",
        "-fa", "on",
        "-ctk", "q8_0",
        "-ctv", "q8_0",
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
        source = "\n".join((
            inspect.getsource(capture.run_once),
            inspect.getsource(capture.cleanup_process_group),
        ))

        self.assertNotRegex(source, r"process\.wait\(\)")
        self.assertRegex(source, r"process\.wait\(timeout=")
        self.assertNotRegex(source, r"\.join\(\)")
        self.assertRegex(source, r"\.join\(timeout=")

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

    def test_supervisor_fit_on_and_long_gpu_layers_alias_normalize_for_replay(self) -> None:
        explore = ["--fit", "on", "--metrics", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0", "--jinja", "-v"]
        replay = ["-c", "0", "--n-gpu-layers", "-1", "-ctk", "q8_0", "-ctv", "q8_0", "--flash-attn", "on", "--jinja", "--metrics"]
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
            "-m", "model.gguf",
            "--host", "127.0.0.1",
            "--port", "19150",
            "--parallel", "1",
            "--no-warmup",
            "--fit", "on",
            "--metrics",
            "-c", "4096",
            "-fa", "on",
            "-ctk", "q8_0",
            "-ctv", "q8_0",
        ]
        expected = [
            "-m", "model.gguf",
            "--parallel", "1",
            "--fit", "on",
            "-c", "4096",
            "-fa", "on",
            "-ctk", "q8_0",
            "-ctv", "q8_0",
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
            capture.capture_host(Path("/tmp/llama-server"))

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
            source.write_text(json.dumps({"path": "/home/sam/models/model.gguf", "argv": ["/opt/runtime/llama-server"]}), encoding="utf-8")
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

    def test_sanitize_redacts_gpu_uuid_and_rejects_identifier_leakage(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "raw.json"
            output = root / "safe.json"
            source.write_text(
                json.dumps({
                    "gpu": "GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5",
                    "mig_legacy": "MIG-GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5/7/3",
                    "mig_current": "MIG-3f3f2b11-0f24-4f10-a2d8-65d7bd9a4c99",
                }),
                encoding="utf-8",
            )

            capture.sanitize_artifact(source, output, [])

            sanitized = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("<gpu-uuid>", sanitized["gpu"])
            self.assertEqual("<gpu-uuid>", sanitized["mig_legacy"])
            self.assertEqual("<gpu-uuid>", sanitized["mig_current"])
            self.assertNotRegex(json.dumps(sanitized), r"(?:GPU|MIG)-[0-9a-f]")


if __name__ == "__main__":
    unittest.main()
