"""Runtime, framework, and comparison identity verification."""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

from .contracts import (
    FRAMEWORK_COMMAND_PARTITIONS,
    FRAMEWORK_PACKAGE_NAMES,
    CaptureError,
    is_sha256,
    require_string,
    sha256_file,
    sha256_tree,
)
from .process import capture_text


def verify_identity(label: str, entry: dict[str, Any]) -> dict[str, Any]:
    raw_path = Path(require_string(entry, "path", label)).expanduser().resolve()
    expected = require_string(entry, "sha256", label).lower()
    if len(expected) != 64 or any(char not in "0123456789abcdef" for char in expected):
        raise CaptureError(f"{label}.sha256 must be a lowercase SHA-256 digest")
    actual = sha256_tree(raw_path)
    if actual != expected:
        raise CaptureError(f"{label} hash mismatch: expected {expected}, got {actual} ({raw_path})")
    return {
        **entry,
        "path": str(raw_path),
        "sha256_verified": True,
        "runtime_local_dependencies": runtime_local_dependencies(raw_path)
        if raw_path.is_file() and os.access(raw_path, os.X_OK)
        else None,
    }


def runtime_local_dependencies(binary: Path) -> dict[str, Any]:
    probe = capture_text(["ldd", str(binary)])
    dependencies: list[dict[str, str]] = []
    if probe["exit_code"] == 0:
        binary_directory = binary.parent.resolve()
        for line in probe["stdout"].splitlines():
            candidate = (
                line.partition("=>")[2].strip().split(" ", 1)[0] if "=>" in line else line.strip().split(" ", 1)[0]
            )
            if not candidate.startswith("/"):
                continue
            path = Path(candidate).resolve()
            if path.is_file() and path.parent == binary_directory:
                dependencies.append({"name": path.name, "sha256": sha256_file(path)})
    dependencies.sort(key=lambda item: item["name"])
    manifest_digest = hashlib.sha256(
        json.dumps(dependencies, separators=(",", ":"), sort_keys=True).encode()
    ).hexdigest()
    return {"ldd": probe, "files": dependencies, "manifest_sha256": manifest_digest}


def git_text(cwd: Path, arguments: list[str], context: str) -> str:
    try:
        completed = subprocess.run(
            ["git", "-C", str(cwd), *arguments],
            text=True,
            capture_output=True,
            timeout=10,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise CaptureError(f"{context} Git probe failed: {exc}") from exc
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or f"exit {completed.returncode}"
        raise CaptureError(f"{context} Git probe failed: {detail}")
    return completed.stdout.strip()


def central_package_versions(path: Path) -> dict[str, str]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise CaptureError(f"Could not parse central package pins from {path}: {exc}") from exc
    versions: dict[str, str] = {}
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] != "PackageVersion":
            continue
        name = element.get("Include") or element.get("Update")
        version = element.get("Version")
        if name and version:
            versions[name] = version
    return versions


def is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def verify_framework_identity(framework: dict[str, Any], commands: list[dict[str, Any]]) -> dict[str, Any]:
    protected = [
        (index, command)
        for index, command in enumerate(commands)
        if command.get("partition", "native-performance") in FRAMEWORK_COMMAND_PARTITIONS
    ]
    declaration = {
        key: framework.get(key)
        for key in ("source_commit", "maf_version", "meai_version", "openai_version", "mcp_version")
        if key in framework
    }
    if not protected:
        return {"required": False, "verified": True, "declaration": declaration, "command_trees": []}

    package_pins = framework.get("central_package_pins")
    if not isinstance(package_pins, dict):
        raise CaptureError(
            "spec.framework.central_package_pins must be an identity object for framework/application commands"
        )
    package_pins_path = Path(require_string(package_pins, "path", "spec.framework.central_package_pins"))
    if package_pins_path.is_absolute():
        raise CaptureError("spec.framework.central_package_pins.path must be relative to each command Git root")

    command_trees: list[dict[str, Any]] = []
    for index, command in protected:
        context = f"spec.commands[{index}]"
        raw_cwd = command.get("cwd")
        if not isinstance(raw_cwd, str) or not raw_cwd:
            raise CaptureError(f"{context}.cwd is required for framework/application commands")
        cwd = Path(raw_cwd).expanduser().resolve()
        if not cwd.is_dir():
            raise CaptureError(f"{context}.cwd must be an existing directory")
        git_root = Path(git_text(cwd, ["rev-parse", "--show-toplevel"], context)).resolve()
        head = git_text(cwd, ["rev-parse", "HEAD"], context)
        declared_commit = require_string(framework, "source_commit", "spec.framework")
        resolved_declared = git_text(cwd, ["rev-parse", f"{declared_commit}^{{commit}}"], context)
        if resolved_declared != head:
            raise CaptureError(
                f"{context} Git HEAD {head} does not match spec.framework.source_commit {declared_commit}"
            )
        status = git_text(cwd, ["status", "--porcelain=v1", "--untracked-files=all"], context)
        if status:
            raise CaptureError(f"{context} Git tree is not clean; framework evidence cannot be attributed to {head}")

        resolved_pins = (git_root / package_pins_path).resolve()
        if not is_relative_to(resolved_pins, git_root):
            raise CaptureError("spec.framework.central_package_pins.path escapes the command Git root")
        verified_pins = verify_identity(
            "spec.framework.central_package_pins",
            {**package_pins, "path": str(resolved_pins)},
        )
        resolved_versions = central_package_versions(resolved_pins)
        for field, package_name in FRAMEWORK_PACKAGE_NAMES.items():
            if field not in framework:
                continue
            declared_version = require_string(framework, field, "spec.framework")
            actual_version = resolved_versions.get(package_name)
            if actual_version != declared_version:
                raise CaptureError(
                    f"{context} declares {field}={declared_version}, but {package_name} is pinned to "
                    f"{actual_version!r} in {resolved_pins}"
                )

        assemblies = command.get("framework_assemblies")
        if not isinstance(assemblies, list) or not assemblies or not all(isinstance(item, dict) for item in assemblies):
            raise CaptureError(f"{context}.framework_assemblies must be a non-empty array of identity objects")
        verified_assemblies = []
        for assembly_index, assembly in enumerate(assemblies):
            assembly_context = f"{context}.framework_assemblies[{assembly_index}]"
            assembly_path = Path(require_string(assembly, "path", assembly_context)).expanduser()
            if not assembly_path.is_absolute():
                assembly_path = git_root / assembly_path
            assembly_path = assembly_path.resolve()
            if not is_relative_to(assembly_path, git_root):
                raise CaptureError(f"{assembly_context}.path must identify a built assembly under the command Git root")
            if not assembly_path.is_file():
                raise CaptureError(f"{assembly_context}.path must identify an existing assembly file")
            verified_assemblies.append(verify_identity(assembly_context, {**assembly, "path": str(assembly_path)}))
        command_trees.append(
            {
                "command": require_string(command, "name", context),
                "partition": command.get("partition"),
                "cwd": str(cwd),
                "git_root": str(git_root),
                "git_head": head,
                "git_head_verified": True,
                "git_clean": True,
                "central_package_pins": verified_pins,
                "resolved_package_versions": {
                    package_name: resolved_versions.get(package_name)
                    for field, package_name in FRAMEWORK_PACKAGE_NAMES.items()
                    if field in framework
                },
                "declared_versions_verified": True,
                "assemblies": verified_assemblies,
            }
        )
    return {
        "required": True,
        "verified": True,
        "declaration": declaration,
        "command_trees": command_trees,
    }


def identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    identity = artifact.get("verified_identity", {})
    host = artifact.get("host", {})
    return {
        "models": [
            {key: item.get(key) for key in ("name", "role", "quant", "sha256")} for item in identity.get("models", [])
        ],
        "corpus": {key: identity.get("corpus", {}).get(key) for key in ("name", "sha256")},
        "runtime": {
            **{key: identity.get("runtime", {}).get(key) for key in ("tag", "provenance", "backend", "sha256")},
            "dependency_manifest_sha256": (identity.get("runtime", {}).get("runtime_local_dependencies") or {}).get(
                "manifest_sha256"
            ),
            "auxiliary_binaries": [
                {
                    **{key: item.get(key) for key in ("name", "sha256")},
                    "dependency_manifest_sha256": (item.get("runtime_local_dependencies") or {}).get("manifest_sha256"),
                }
                for item in identity.get("runtime", {}).get("auxiliary_binaries", [])
            ],
        },
        "machine": {key: host.get(key) for key in ("os", "kernel", "architecture", "cpu", "logical_cpu_count")},
        "devices": host.get("runtime_devices"),
    }


def framework_identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    framework = artifact.get("framework")
    identity = artifact.get("verified_identity", {}).get("framework")
    if not isinstance(framework, dict) or not isinstance(identity, dict) or identity.get("verified") is not True:
        raise CaptureError("Artifact lacks verified framework identity")
    declaration = {
        key: framework.get(key)
        for key in ("source_commit", "maf_version", "meai_version", "openai_version", "mcp_version")
        if key in framework
    }
    if identity.get("declaration") != declaration:
        raise CaptureError("Artifact framework declaration differs from its verified framework identity")
    command_trees = identity.get("command_trees")
    if not isinstance(command_trees, list):
        raise CaptureError("Artifact verified framework identity has invalid command_trees")
    if identity.get("required") is True:
        if not command_trees:
            raise CaptureError("Artifact framework/application commands lack verified command-tree identity")
        for item in command_trees:
            if (
                not isinstance(item, dict)
                or item.get("git_head_verified") is not True
                or item.get("git_clean") is not True
                or item.get("declared_versions_verified") is not True
                or not isinstance(item.get("assemblies"), list)
                or not item["assemblies"]
            ):
                raise CaptureError("Artifact contains incomplete verified framework command-tree identity")
    return {
        "declaration": declaration,
        "command_trees": command_trees,
    }


def verified_runtime_identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    runtime = artifact.get("verified_identity", {}).get("runtime")
    if not isinstance(runtime, dict) or runtime.get("sha256_verified") is not True:
        raise CaptureError("Artifact lacks verified runtime identity")
    if any(
        not isinstance(runtime.get(key), str) or not runtime[key].strip() for key in ("tag", "provenance", "backend")
    ) or not is_sha256(runtime.get("sha256")):
        raise CaptureError("Artifact lacks verified runtime identity")
    dependencies = runtime.get("runtime_local_dependencies")
    if not isinstance(dependencies, dict) or not is_sha256(dependencies.get("manifest_sha256")):
        raise CaptureError("Artifact lacks verified runtime dependency identity")
    auxiliaries = runtime.get("auxiliary_binaries", [])
    if not isinstance(auxiliaries, list):
        raise CaptureError("Artifact has invalid verified auxiliary runtime identities")
    projected_auxiliaries: list[dict[str, Any]] = []
    for auxiliary in auxiliaries:
        if (
            not isinstance(auxiliary, dict)
            or auxiliary.get("sha256_verified") is not True
            or not isinstance(auxiliary.get("name"), str)
            or not auxiliary["name"].strip()
            or not is_sha256(auxiliary.get("sha256"))
        ):
            raise CaptureError("Artifact has invalid verified auxiliary runtime identities")
        auxiliary_dependencies = auxiliary.get("runtime_local_dependencies")
        if not isinstance(auxiliary_dependencies, dict) or not is_sha256(auxiliary_dependencies.get("manifest_sha256")):
            raise CaptureError("Artifact lacks verified auxiliary runtime dependency identity")
        projected_auxiliaries.append(
            {
                "name": auxiliary["name"],
                "sha256": auxiliary["sha256"],
                "dependency_manifest_sha256": auxiliary_dependencies["manifest_sha256"],
            }
        )
    return {
        "tag": runtime["tag"],
        "provenance": runtime["provenance"],
        "backend": runtime["backend"],
        "sha256": runtime["sha256"],
        "dependency_manifest_sha256": dependencies["manifest_sha256"],
        "auxiliary_binaries": projected_auxiliaries,
    }


def verified_framework_identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    projected = framework_identity_projection(artifact)
    framework_identity = artifact["verified_identity"]["framework"]
    required = framework_identity.get("required")
    if not isinstance(required, bool) or framework_identity.get("verified") is not True:
        raise CaptureError("Artifact lacks a valid verified framework identity decision")
    declaration = projected["declaration"]
    if any(
        not isinstance(declaration.get(key), str) or not declaration[key].strip()
        for key in ("source_commit", "maf_version", "meai_version", "openai_version")
    ):
        raise CaptureError("Artifact contains an incomplete framework declaration")
    if "mcp_version" in declaration and (
        not isinstance(declaration["mcp_version"], str) or not declaration["mcp_version"].strip()
    ):
        raise CaptureError("Artifact contains an incomplete framework declaration")
    command_trees = framework_identity["command_trees"]
    if required is False:
        if command_trees != []:
            raise CaptureError("Artifact has framework command trees when framework verification is not required")
        return {"required": False, **projected}
    if not command_trees:
        raise CaptureError("Artifact lacks verified framework command-tree identity")
    for command_tree in command_trees:
        if (
            not isinstance(command_tree, dict)
            or not isinstance(command_tree.get("command"), str)
            or not command_tree["command"].strip()
            or not isinstance(command_tree.get("git_head"), str)
            or not command_tree["git_head"].strip()
            or command_tree.get("git_head_verified") is not True
            or command_tree.get("git_clean") is not True
            or command_tree.get("declared_versions_verified") is not True
        ):
            raise CaptureError("Artifact contains incomplete verified framework command-tree identity")
        pins = command_tree.get("central_package_pins")
        assemblies = command_tree.get("assemblies")
        if (
            not isinstance(pins, dict)
            or pins.get("sha256_verified") is not True
            or not is_sha256(pins.get("sha256"))
            or not isinstance(assemblies, list)
            or not assemblies
            or not all(
                isinstance(assembly, dict)
                and assembly.get("sha256_verified") is True
                and isinstance(assembly.get("name"), str)
                and bool(assembly["name"].strip())
                and is_sha256(assembly.get("sha256"))
                for assembly in assemblies
            )
        ):
            raise CaptureError("Artifact contains unverified framework file identities")
    return {"required": True, **projected}


def immutable_gate_identity_projection(artifact: dict[str, Any]) -> dict[str, Any]:
    verified_identity = artifact.get("verified_identity")
    host = artifact.get("host")
    if not isinstance(verified_identity, dict) or not isinstance(host, dict):
        raise CaptureError("Artifact lacks verified immutable identity")
    models = verified_identity.get("models")
    corpus = verified_identity.get("corpus")
    if (
        not isinstance(models, list)
        or not models
        or not all(
            isinstance(model, dict)
            and model.get("sha256_verified") is True
            and all(isinstance(model.get(key), str) and bool(model[key].strip()) for key in ("name", "role", "quant"))
            and is_sha256(model.get("sha256"))
            for model in models
        )
        or not isinstance(corpus, dict)
        or corpus.get("sha256_verified") is not True
        or not isinstance(corpus.get("name"), str)
        or not corpus["name"].strip()
        or not is_sha256(corpus.get("sha256"))
    ):
        raise CaptureError("Artifact lacks verified model or corpus identity")
    for optional_key in ("repository", "revision"):
        if any(
            optional_key in model and (not isinstance(model[optional_key], str) or not model[optional_key].strip())
            for model in models
        ):
            raise CaptureError("Artifact contains an incomplete model identity")
    if "revision" in corpus and (not isinstance(corpus["revision"], str) or not corpus["revision"].strip()):
        raise CaptureError("Artifact contains an incomplete corpus identity")
    if (
        any(
            not isinstance(host.get(key), str) or not host[key].strip()
            for key in ("os", "kernel", "architecture", "cpu")
        )
        or isinstance(host.get("logical_cpu_count"), bool)
        or not isinstance(host.get("logical_cpu_count"), int)
        or host["logical_cpu_count"] <= 0
    ):
        raise CaptureError("Artifact lacks complete machine identity")
    runtime_devices = host.get("runtime_devices")
    if (
        not isinstance(runtime_devices, dict)
        or not isinstance(runtime_devices.get("argv"), list)
        or not runtime_devices["argv"]
        or not all(isinstance(argument, str) and argument for argument in runtime_devices["argv"])
        or not isinstance(runtime_devices.get("available"), bool)
        or (
            runtime_devices.get("exit_code") is not None
            and (isinstance(runtime_devices["exit_code"], bool) or not isinstance(runtime_devices["exit_code"], int))
        )
        or not isinstance(runtime_devices.get("stdout"), str)
        or not isinstance(runtime_devices.get("stderr"), str)
    ):
        raise CaptureError("Artifact lacks complete runtime device identity")
    return {
        "models": [
            {
                key: model.get(key)
                for key in ("name", "role", "quant", "repository", "revision", "sha256")
                if key in model
            }
            for model in models
        ],
        "corpus": {key: corpus.get(key) for key in ("name", "revision", "sha256") if key in corpus},
        "machine": {key: host.get(key) for key in ("os", "kernel", "architecture", "cpu", "logical_cpu_count")},
        "devices": runtime_devices,
    }
