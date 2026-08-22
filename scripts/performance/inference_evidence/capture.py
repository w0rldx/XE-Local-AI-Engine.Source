"""Baseline and fit/replay evidence capture workflows."""

from __future__ import annotations

import re
import shlex
from pathlib import Path
from typing import Any

from .contracts import (
    FIT_FLAG_CANONICAL,
    FIT_FLAGS_WITH_VALUE,
    FIT_HELPER_ARGS_WITH_VALUE,
    FIT_HELPER_VALUELESS_ARGS,
    SCHEMA_VERSION,
    CaptureError,
    load_json,
    require_string,
    sha256_file,
    utc_now,
    validate_spec,
    write_json,
)
from .identity import (
    framework_identity_projection,
    identity_projection,
    verify_framework_identity,
    verify_identity,
)
from .process import (
    capture_host,
    command_argv,
    global_gpu_free_mib,
    global_gpu_used_mib,
    process_budget_free_mib,
    run_command,
)


def capture_baseline(spec_path: Path, output_path: Path) -> None:
    spec = load_json(spec_path)
    validate_spec(spec, "inference-benchmark-capture")
    models = spec.get("models")
    if not isinstance(models, list) or not models:
        raise CaptureError("spec.models must be a non-empty array")
    verified_models = []
    for index, model in enumerate(models):
        if not isinstance(model, dict):
            raise CaptureError(f"spec.models[{index}] must be an object")
        for key in ("name", "role", "quant"):
            require_string(model, key, f"spec.models[{index}]")
        verified_models.append(verify_identity(f"spec.models[{index}]", model))
    corpus = spec.get("corpus")
    runtime = spec.get("runtime")
    if not isinstance(corpus, dict) or not isinstance(runtime, dict):
        raise CaptureError("spec.corpus and spec.runtime must be objects")
    require_string(corpus, "name", "spec.corpus")
    require_string(runtime, "tag", "spec.runtime")
    require_string(runtime, "provenance", "spec.runtime")
    require_string(runtime, "backend", "spec.runtime")
    verified_corpus = verify_identity("spec.corpus", corpus)
    verified_runtime = verify_identity("spec.runtime", runtime)
    auxiliaries = runtime.get("auxiliary_binaries", [])
    if not isinstance(auxiliaries, list) or not all(isinstance(item, dict) for item in auxiliaries):
        raise CaptureError("spec.runtime.auxiliary_binaries must be an array of identity objects")
    verified_auxiliaries = [
        verify_identity(f"spec.runtime.auxiliary_binaries[{index}]", item) for index, item in enumerate(auxiliaries)
    ]
    verified_runtime["auxiliary_binaries"] = verified_auxiliaries
    commands = spec.get("commands")
    if not isinstance(commands, list) or not commands:
        raise CaptureError("spec.commands must be a non-empty array")
    if not all(isinstance(command, dict) for command in commands):
        raise CaptureError("Every spec.commands entry must be an object")
    benchmark = spec.get("benchmark")
    framework = spec.get("framework")
    coverage = spec.get("coverage")
    if not isinstance(benchmark, dict) or not isinstance(framework, dict) or not isinstance(coverage, dict):
        raise CaptureError("spec.framework, spec.benchmark, and spec.coverage must be objects")
    for key in ("source_commit", "maf_version", "meai_version", "openai_version"):
        require_string(framework, key, "spec.framework")
    for key in ("cache_state", "acceptance_rule"):
        require_string(benchmark, key, "spec.benchmark")
    gaps = coverage.get("unvalidated")
    if (
        not isinstance(gaps, list)
        or not gaps
        or not all(isinstance(item, dict) and item.get("target") and item.get("reason") for item in gaps)
    ):
        raise CaptureError("spec.coverage.unvalidated must explicitly list target/reason objects")

    framework_identity = verify_framework_identity(framework, commands)
    started_at = utc_now()
    results = [run_command(command, f"spec.commands[{index}]") for index, command in enumerate(commands)]
    artifact = {
        "schema_version": SCHEMA_VERSION,
        "kind": "inference-benchmark-evidence",
        "capture_id": spec["capture_id"],
        "phase": spec["phase"],
        "started_at_utc": started_at,
        "completed_at_utc": utc_now(),
        "spec_sha256": sha256_file(spec_path),
        "source_spec": spec,
        "verified_identity": {
            "models": verified_models,
            "corpus": verified_corpus,
            "runtime": verified_runtime,
            "framework": framework_identity,
        },
        "host": capture_host(Path(verified_runtime["path"])),
        "framework": framework,
        "benchmark": benchmark,
        "coverage": coverage,
        "commands": results,
    }
    write_json(output_path, artifact)


def strip_one_verbose(argv: list[str]) -> list[str]:
    result = list(argv)
    for flag in ("-v", "--verbose"):
        if flag in result:
            result.remove(flag)
            return result
    return result


def extract_fit_flags(argv: list[str]) -> dict[str, list[str]]:
    parsed: dict[str, list[str]] = {}
    index = 0
    while index < len(argv):
        flag = argv[index]
        if flag in FIT_FLAGS_WITH_VALUE:
            if index + 1 >= len(argv):
                raise CaptureError(f"Launch vector ends after {flag}")
            canonical = FIT_FLAG_CANONICAL.get(flag, flag)
            parsed.setdefault(canonical, []).append(argv[index + 1])
            index += 2
        else:
            index += 1
    return parsed


def project_fit_helper_arguments(server_argv: list[str]) -> list[str]:
    """Mirror LlamaFitParamsProcessRunner.BuildArguments over one production server vector."""
    projected: list[str] = []
    index = 0
    while index < len(server_argv):
        argument = server_argv[index]
        if argument in FIT_HELPER_ARGS_WITH_VALUE:
            if index + 1 >= len(server_argv):
                raise CaptureError(f"Production Explore vector ends after helper-relevant argument {argument}")
            projected.extend((argument, server_argv[index + 1]))
            index += 2
        elif argument in FIT_HELPER_VALUELESS_ARGS:
            projected.append(argument)
            index += 1
        else:
            index += 1
    return projected


def option_values(argv: list[str], option: str) -> list[str]:
    values: list[str] = []
    for index, argument in enumerate(argv):
        if argument != option:
            continue
        if index + 1 >= len(argv):
            raise CaptureError(f"Launch vector ends after {option}")
        values.append(argv[index + 1])
    return values


def validate_kv_flash_equivalence(explore_flags: dict[str, list[str]], replay_flags: dict[str, list[str]]) -> None:
    for flag in ("-ctk", "-ctv", "-fa"):
        explore_values = explore_flags.get(flag, [])
        replay_values = replay_flags.get(flag, [])
        if len(explore_values) > 1 or len(replay_values) > 1:
            raise CaptureError(f"Explore and replay must contain at most one {flag} value")
        if explore_values != replay_values:
            raise CaptureError(
                f"Explore and replay KV/flash-attention settings differ for {flag}: "
                f"explore={explore_values}, replay={replay_values}"
            )

    kv_k = explore_flags.get("-ctk", [])
    kv_v = explore_flags.get("-ctv", [])
    flash = explore_flags.get("-fa", [])
    if (kv_k or kv_v or flash) and (len(kv_k) != 1 or kv_k != kv_v or flash != ["on"]):
        raise CaptureError("Optimized Explore/replay must use matching -ctk/-ctv values with flash attention set to on")


def validate_concrete_fit_flags(fitted_flags: dict[str, list[str]]) -> None:
    contexts = fitted_flags.get("-c", [])
    if len(contexts) != 1:
        raise CaptureError("llama-fit-params output must contain exactly one -c value")

    gpu_layers = fitted_flags.get("-ngl", [])
    if len(gpu_layers) != 1:
        raise CaptureError("llama-fit-params output must contain exactly one -ngl value")

    try:
        context = int(contexts[0], 10)
    except ValueError as error:
        raise CaptureError("llama-fit-params -c must be a positive concrete integer") from error
    if context <= 0:
        raise CaptureError("llama-fit-params -c must be a positive concrete integer")

    try:
        placement = int(gpu_layers[0], 10)
    except ValueError as error:
        raise CaptureError("llama-fit-params -ngl must be an integer") from error
    if placement == -1:
        raise CaptureError("llama-fit-params -ngl -1 is automatic placement, not a frozen placement")
    if placement < -2:
        raise CaptureError("llama-fit-params -ngl must be -2 (all layers) or a non-negative layer count")


def normalize_fit_flags(fitted_flags: dict[str, list[str]], verbose_startup: str) -> dict[str, list[str]]:
    normalized = {key: list(values) for key, values in fitted_flags.items()}
    gpu_layers = normalized.get("-ngl", [])
    if gpu_layers == ["-1"]:
        offload_counts = [
            (int(match.group(1)), int(match.group(2)))
            for match in re.finditer(r"offloaded\s+(\d+)/(\d+)\s+layers to GPU", verbose_startup)
        ]
        if not offload_counts or any(offloaded <= 0 or offloaded != total for offloaded, total in offload_counts):
            raise CaptureError(
                "llama-fit-params -ngl -1 requires authoritative full-offload evidence before it can be normalized"
            )
        normalized["-ngl"] = ["-2"]

    validate_concrete_fit_flags(normalized)
    return normalized


def without_fit_semantics(argv: list[str]) -> list[str]:
    result: list[str] = []
    index = 0
    while index < len(argv):
        item = argv[index]
        if item == "--fit":
            index += 2 if index + 1 < len(argv) and argv[index + 1] in {"on", "off"} else 1
        elif item in FIT_FLAGS_WITH_VALUE:
            index += 2
        elif item in {"-v", "--verbose", "--metrics"}:
            # Profiling-only diagnostics do not change fit placement. Explore carries verbosity so startup can prove
            # full offload; both vectors carry --metrics, but replay appends it after role flags.
            index += 1
        else:
            result.append(item)
            index += 1
    return result


def capture_fit(spec_path: Path, output_path: Path) -> None:
    spec = load_json(spec_path)
    validate_spec(spec, "fit-replay-capture")
    binaries = spec.get("binaries")
    if not isinstance(binaries, dict):
        raise CaptureError("spec.binaries must be an object")
    server = verify_identity("spec.binaries.server", binaries.get("server", {}))
    helper = verify_identity("spec.binaries.fit_helper", binaries.get("fit_helper", {}))
    commands = spec.get("commands")
    vectors = spec.get("launch_vectors")
    if not isinstance(commands, dict) or not isinstance(vectors, dict):
        raise CaptureError("spec.commands and spec.launch_vectors must be objects")
    required = ("default_verbosity", "verbose", "fit_params", "explore", "replay")
    if any(not isinstance(commands.get(name), dict) for name in required):
        raise CaptureError(
            "spec.commands must contain default_verbosity, verbose, fit_params, explore, and replay objects"
        )
    default_argv = command_argv(commands["default_verbosity"], "spec.commands.default_verbosity")
    verbose_argv = command_argv(commands["verbose"], "spec.commands.verbose")
    fit_argv = command_argv(commands["fit_params"], "spec.commands.fit_params")
    if Path(default_argv[0]).resolve() != Path(server["path"]) or Path(verbose_argv[0]).resolve() != Path(
        server["path"]
    ):
        raise CaptureError("default_verbosity and verbose must invoke the verified server binary")
    if Path(fit_argv[0]).resolve() != Path(helper["path"]):
        raise CaptureError("fit_params must invoke the verified fit-helper binary")
    if "-v" in default_argv or "--verbose" in default_argv:
        raise CaptureError("default_verbosity must not contain -v/--verbose")
    if strip_one_verbose(verbose_argv) != default_argv:
        raise CaptureError("verbose argv must equal default_verbosity argv plus exactly one -v/--verbose flag")
    explore = vectors.get("explore")
    replay = vectors.get("replay")
    if (
        not isinstance(explore, list)
        or not isinstance(replay, list)
        or not all(isinstance(item, str) for item in explore + replay)
    ):
        raise CaptureError("launch_vectors.explore and replay must be string arrays")
    if option_values(explore, "--fit") != ["on"] or "--fit" in replay:
        raise CaptureError("explore must contain exactly one '--fit on' pair and replay must not contain --fit")
    if explore.count("--metrics") != 1 or replay.count("--metrics") != 1:
        raise CaptureError(
            "production Explore and replay profiling vectors must each contain exactly one --metrics flag"
        )
    verbose_count = sum(explore.count(flag) for flag in ("-v", "--verbose"))
    if verbose_count != 1:
        raise CaptureError("production Explore must contain exactly one -v/--verbose flag")
    if "-v" in replay or "--verbose" in replay:
        raise CaptureError("replay must not contain the profiling-only -v/--verbose flag")
    explore_argv = command_argv(commands["explore"], "spec.commands.explore")
    replay_argv = command_argv(commands["replay"], "spec.commands.replay")
    if Path(explore_argv[0]).resolve() != Path(server["path"]) or Path(replay_argv[0]).resolve() != Path(
        server["path"]
    ):
        raise CaptureError("explore and replay must invoke the verified server binary")
    if explore_argv[1:] != explore or replay_argv[1:] != replay:
        raise CaptureError("launch_vectors must exactly equal the explore/replay command argv after the binary path")
    if verbose_argv[1:] != explore:
        raise CaptureError("verbose must use the exact production Explore launch vector")
    projected_helper_argv = project_fit_helper_arguments(explore)
    if fit_argv[1:] != projected_helper_argv:
        raise CaptureError(
            "fit_params argv must exactly equal the production helper projection of Explore: "
            f"expected={projected_helper_argv}, actual={fit_argv[1:]}"
        )

    default_result = run_command(commands["default_verbosity"], "spec.commands.default_verbosity")
    verbose_result = run_command(commands["verbose"], "spec.commands.verbose")
    fit_result = run_command(commands["fit_params"], "spec.commands.fit_params")
    explore_result = run_command(commands["explore"], "spec.commands.explore")
    replay_result = run_command(commands["replay"], "spec.commands.replay")
    fit_stdout = fit_result["runs"][-1]["stdout"]
    fit_line = next((line.strip() for line in reversed(fit_stdout.splitlines()) if line.strip().startswith("-c ")), "")
    if not fit_line:
        raise CaptureError("llama-fit-params output did not contain a deterministic '-c ...' argument line")
    default_text = default_result["runs"][-1]["stdout"] + "\n" + default_result["runs"][-1]["stderr"]
    verbose_text = verbose_result["runs"][-1]["stdout"] + "\n" + verbose_result["runs"][-1]["stderr"]
    explore_text = explore_result["runs"][-1]["stdout"] + "\n" + explore_result["runs"][-1]["stderr"]
    fitted = shlex.split(fit_line, posix=True)
    fitted_flags = extract_fit_flags(fitted)
    unexpected_fitted_flags = set(fitted_flags) - {"-c", "-ngl", "-ts", "-ot"}
    if unexpected_fitted_flags:
        raise CaptureError(
            "llama-fit-params stdout contains unsupported flags outside its machine-readable grammar: "
            f"{sorted(unexpected_fitted_flags)}"
        )
    normalized_fitted_flags = normalize_fit_flags(fitted_flags, explore_text)
    explore_flags = extract_fit_flags(explore)
    replay_flags = extract_fit_flags(replay)
    validate_concrete_fit_flags(replay_flags)
    if normalized_fitted_flags != {key: replay_flags.get(key, []) for key in normalized_fitted_flags}:
        raise CaptureError(
            "Replay placement differs from normalized llama-fit-params output: "
            f"fitted={fitted_flags}, normalized={normalized_fitted_flags}, replay={replay_flags}"
        )
    validate_kv_flash_equivalence(explore_flags, replay_flags)
    if without_fit_semantics(explore) != without_fit_semantics(replay):
        raise CaptureError("Explore and replay non-fit launch arguments are not byte-equivalent")
    acceptance = spec.get("resource_acceptance")
    if not isinstance(acceptance, dict):
        raise CaptureError("spec.resource_acceptance must be an object")
    tolerance = acceptance.get("max_delta_percent")
    if not isinstance(tolerance, (int, float)) or tolerance < 0:
        raise CaptureError("spec.resource_acceptance.max_delta_percent must be a non-negative number")
    explore_rss = explore_result["runs"][-1]["peak_rss_bytes"]
    replay_rss = replay_result["runs"][-1]["peak_rss_bytes"]
    rss_delta_percent = None
    rss_within_tolerance = None
    if explore_rss and replay_rss:
        rss_delta_percent = abs(replay_rss - explore_rss) / explore_rss * 100
        rss_within_tolerance = rss_delta_percent <= tolerance
        if not rss_within_tolerance:
            raise CaptureError(f"Explore/replay peak RSS delta {rss_delta_percent:.2f}% exceeds {tolerance:.2f}%")
    explore_gpu_used = global_gpu_used_mib(explore_result["runs"][-1]["ambient_during"])
    replay_gpu_used = global_gpu_used_mib(replay_result["runs"][-1]["ambient_during"])
    gpu_delta_percent = None
    gpu_within_tolerance = None
    if explore_gpu_used and replay_gpu_used:
        gpu_delta_percent = abs(replay_gpu_used - explore_gpu_used) / explore_gpu_used * 100
        gpu_within_tolerance = gpu_delta_percent <= tolerance
        if not gpu_within_tolerance:
            raise CaptureError(
                f"Explore/replay global GPU-used delta {gpu_delta_percent:.2f}% exceeds {tolerance:.2f}%"
            )
    host = capture_host(Path(server["path"]))
    process_free = process_budget_free_mib(host["runtime_devices"])
    explore_global_free = global_gpu_free_mib(explore_result["runs"][-1]["ambient_during"])
    replay_global_free = global_gpu_free_mib(replay_result["runs"][-1]["ambient_during"])
    artifact = {
        "schema_version": SCHEMA_VERSION,
        "kind": "fit-replay-evidence",
        "capture_id": spec["capture_id"],
        "phase": spec["phase"],
        "completed_at_utc": utc_now(),
        "spec_sha256": sha256_file(spec_path),
        "verified_identity": {"server": server, "fit_helper": helper},
        "host": host,
        "captures": {
            "default_verbosity": default_result,
            "verbose": verbose_result,
            "fit_params": fit_result,
            "explore": explore_result,
            "replay": replay_result,
        },
        "launch_vectors": {"explore": explore, "replay": replay, "fitted_stdout_argv": fitted},
        "equivalence": {
            "fitted_flags": fitted_flags,
            "normalized_fitted_flags": normalized_fitted_flags,
            "explore_policy_flags": {key: explore_flags.get(key, []) for key in ("-ctk", "-ctv", "-fa")},
            "replay_flags": replay_flags,
            "non_fit_vector_equal": True,
            "placement_equal": True,
            "kv_flash_equal": True,
            "metrics_enabled_for_both": True,
            "peak_rss_delta_percent": rss_delta_percent,
            "peak_rss_within_tolerance": rss_within_tolerance,
            "global_gpu_used_delta_percent": gpu_delta_percent,
            "global_gpu_used_within_tolerance": gpu_within_tolerance,
            "resource_tolerance_percent": tolerance,
            "global_vram_samples": {
                "explore": explore_result["runs"][-1]["ambient_during"],
                "replay": replay_result["runs"][-1]["ambient_during"],
            },
            "vram_semantics": {
                "global_free_mib": {"explore": explore_global_free, "replay": replay_global_free},
                "process_budget_free_mib": process_free,
                "process_minus_global_free_mib": {
                    "explore": None
                    if process_free is None or explore_global_free is None
                    else process_free - explore_global_free,
                    "replay": None
                    if process_free is None or replay_global_free is None
                    else process_free - replay_global_free,
                },
                "interpretation": (
                    "Global free VRAM governs contention/invalidation; process-budget VRAM describes "
                    "this process's WDDM/CUDA fit budget. Divergence is reported, never averaged."
                ),
            },
            "verbosity_evidence": {
                "default_fit_detail_lines": default_text.count("common_params_fit_impl"),
                "verbose_fit_detail_lines": verbose_text.count("common_params_fit_impl"),
                "helper_stdout_argv": fitted,
            },
        },
        "coverage": spec.get("coverage", {}),
    }
    write_json(output_path, artifact)


def compare_artifacts(baseline_path: Path, candidate_path: Path, output_path: Path) -> None:
    baseline = load_json(baseline_path)
    candidate = load_json(candidate_path)
    if (
        baseline.get("kind") != "inference-benchmark-evidence"
        or candidate.get("kind") != "inference-benchmark-evidence"
    ):
        raise CaptureError("compare requires two inference-benchmark-evidence artifacts")
    baseline_identity = identity_projection(baseline)
    candidate_identity = identity_projection(candidate)
    mismatches = [key for key in baseline_identity if baseline_identity[key] != candidate_identity[key]]
    if mismatches:
        raise CaptureError("Artifacts are not comparable; identity differs in: " + ", ".join(mismatches))
    baseline_framework_identity = framework_identity_projection(baseline)
    candidate_framework_identity = framework_identity_projection(candidate)
    base_commands = {item["name"]: item for item in baseline.get("commands", [])}
    candidate_commands = {item["name"]: item for item in candidate.get("commands", [])}
    if set(base_commands) != set(candidate_commands):
        raise CaptureError("Artifacts do not contain the same command names")
    comparison: dict[str, Any] = {}
    for name in sorted(base_commands):
        if base_commands[name].get("argv_sha256") != candidate_commands[name].get("argv_sha256"):
            raise CaptureError(f"Command {name!r} used a different argv vector")
        metrics: dict[str, Any] = {}
        base_metrics = base_commands[name].get("aggregates", {})
        candidate_metrics = candidate_commands[name].get("aggregates", {})
        for metric in sorted(set(base_metrics) & set(candidate_metrics)):
            old = base_metrics[metric]["median"]
            new = candidate_metrics[metric]["median"]
            metrics[metric] = {
                "baseline_median": old,
                "candidate_median": new,
                "delta_percent": None if old == 0 else ((new - old) / old) * 100,
            }
        comparison[name] = metrics
    write_json(
        output_path,
        {
            "schema_version": SCHEMA_VERSION,
            "kind": "inference-benchmark-comparison",
            "baseline": str(baseline_path.resolve()),
            "candidate": str(candidate_path.resolve()),
            "identity_equal": True,
            "framework_identity_equal": baseline_framework_identity == candidate_framework_identity,
            "framework_identity": {
                "baseline": baseline_framework_identity,
                "candidate": candidate_framework_identity,
            },
            "commands": comparison,
            "generated_at_utc": utc_now(),
        },
    )
