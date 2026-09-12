#!/usr/bin/env python3
"""Build the External Apps catalog document from the per-application authoring sources.

The engine never parses Compose. This script is the offline pipeline that turns an upstream
``compose.yml`` plus the two authoring files beside it into ``dist/applications.json``, the single
document the engine loads (bundled as an embedded seed, optionally refreshed from a pinned URL).

It validates only what a converter alone can get wrong -- the C1..C7 gates below, the file
base64/sha256/size invariants, the manifest fingerprint and the ``manifestVersion`` bump that has to
accompany a changed fingerprint. ``ExternalAppCatalogValidator`` on the
C# side is the sole authority on manifest validity; duplicating ~50 rules here would only generate
drift between the two.

Usage::

    uv run python catalog/external-apps/tools/build_catalog.py [--check]

Default writes ``dist/applications.json`` and copies it byte-for-byte to the embedded seed path.
``--check`` rebuilds in memory and exits non-zero when either the committed document or the embedded
seed differs from it, printing a unified diff; ``generatedAtUtc`` is normalised first, because it is
the current UTC time on every build and comparing it would make ``--check`` permanently red.

There is deliberately no single-application mode. Every write covers the whole catalog: a filtered
build would write a document holding one application over both outputs and delete the rest.
"""

from __future__ import annotations

import argparse
import base64
import difflib
import hashlib
import json
import re
import shlex
import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import yaml

TOOLS_DIR = Path(__file__).resolve().parent
CATALOG_ROOT = TOOLS_DIR.parent
REPO_ROOT = CATALOG_ROOT.parents[1]
APPLICATIONS_DIR = CATALOG_ROOT / "applications"
DIST_PATH = CATALOG_ROOT / "dist" / "applications.json"
SEED_PATH = (
    REPO_ROOT
    / "XE-Local-AI-Engine.Client.Application"
    / "Services"
    / "ExternalApps"
    / "Catalog"
    / "external-apps-catalog.seed.json"
)

SCHEMA_VERSION = 1
HASH_KEY = "manifestSha256"
MAX_FILE_BYTES = 64 * 1024

DIGEST_PINNED_IMAGE = re.compile(r"^[^\s@]+@sha256:[0-9a-f]{64}$")
DURATION = re.compile(r"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$")

# C3 -- compose keys the engine has no model for. A manifest that silently dropped any of these
# would install something weaker than what upstream tested, so the build fails instead.
REJECTED_SERVICE_KEYS = (
    "privileged",
    "devices",
    "network_mode",
    "pid",
    "ipc",
    "userns_mode",
    "security_opt",
    "sysctls",
    "ulimits",
    "deploy",
    "cgroup_parent",
)
REJECTED_DOCUMENT_KEYS = ("secrets", "configs", "profiles", "extends", "include")
ACCEPTED_RESTART_POLICIES = ("unless-stopped", "no")
DEPENDS_ON_CONDITIONS = {"service_started": "started", "service_healthy": "healthy"}
HOST_GATEWAY = "host-gateway"


class CatalogBuildError(Exception):
    """A converter gate rejected the authoring source. The message names the application and rule."""


def manifest_fingerprint(manifest: dict[str, Any]) -> str:
    """Return the canonical lowercase-hex SHA-256 of ``manifest``, excluding its own hash property.

    This is the cross-language contract with ``ExternalAppManifestFingerprint.Compute``: the single
    manifest object, ``manifestSha256`` removed, every key sorted at every level, arrays keeping
    order, compact separators, no ASCII escaping, UTF-8 without BOM or trailing newline. Imported by
    the live-round fixture mutation helper as well as by this build, so a mutated manifest can be
    re-fingerprinted without re-implementing the algorithm.
    """
    payload = {key: value for key, value in manifest.items() if key != HASH_KEY}
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def _fail(application_id: str, rule: str, message: str) -> CatalogBuildError:
    return CatalogBuildError(f"[{application_id}] {rule}: {message}")


def _unescape_dollars(text: str) -> str:
    """Compose escapes a literal ``$`` as ``$$``; the container's own shell sees the single form."""
    return text.replace("$$", "$")


def _split_outside_braces(value: str) -> list[str]:
    """Split a compose short-syntax field on ``:`` while ignoring colons inside ``${...}``.

    ``${APP_DATA_DIR:-./data}:/app/data:z`` has four colons and three fields; a naive split gives
    four wrong ones.
    """
    parts: list[str] = []
    current: list[str] = []
    depth = 0
    index = 0
    while index < len(value):
        char = value[index]
        if char == "$" and value[index + 1 : index + 2] == "{":
            depth += 1
            current.append("${")
            index += 2
            continue
        if char == "}" and depth > 0:
            depth -= 1
        elif char == ":" and depth == 0:
            parts.append("".join(current))
            current = []
            index += 1
            continue
        current.append(char)
        index += 1
    parts.append("".join(current))
    return parts


def _require_int(application_id: str, path: str, value: object) -> int:
    """Coerce an authored JSON number to ``int``, refusing anything that is not integral.

    JSON has a single number type, so ``2048.0`` and ``2048`` are the same value to an author and two
    different types to ``json.load``. A bare passthrough would put a float into the manifest, change its
    fingerprint and hand the C# validator a value it types as a double; a bare ``int()`` would silently
    truncate ``1.9`` to ``1``. Neither is a thing the author asked for, so a non-integral value fails here.
    """
    if isinstance(value, bool):
        raise _fail(application_id, "types", f"{path} must be an integer, got the boolean {value!r}.")
    if isinstance(value, int):
        return value
    if isinstance(value, float) and value.is_integer():
        return int(value)
    raise _fail(application_id, "types", f"{path} must be an integer, got {value!r}.")


def _optional_int(application_id: str, path: str, value: object) -> int | None:
    return None if value is None else _require_int(application_id, path, value)


def _parse_duration_seconds(application_id: str, path: str, value: object) -> int:
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    if not isinstance(value, str):
        raise _fail(application_id, "healthcheck", f"{path} must be a duration string, got {value!r}.")
    match = DURATION.fullmatch(value.strip())
    if match is None or not any(match.groups()):
        raise _fail(application_id, "healthcheck", f"{path} is not a compose duration: {value!r}.")
    hours, minutes, seconds = (int(group) if group else 0 for group in match.groups())
    return hours * 3600 + minutes * 60 + seconds


def _argument_vector(application_id: str, name: str, field: str, raw: object) -> list[str] | None:
    """Normalise a compose ``entrypoint``/``command`` to argv, unescaping ``$$`` in every element."""
    if raw is None:
        return None
    if isinstance(raw, str):
        return [_unescape_dollars(element) for element in shlex.split(raw)]
    if isinstance(raw, list):
        return [_unescape_dollars(str(element)) for element in raw]
    raise _fail(application_id, "C3", f"service '{name}' has a {field} that is neither a string nor a list.")


def _compose_mount_targets(application_id: str, name: str, service: dict[str, Any]) -> list[str]:
    targets: list[str] = []
    for entry in service.get("volumes") or []:
        if isinstance(entry, dict):
            target = entry.get("target")
            if not isinstance(target, str):
                raise _fail(application_id, "C5", f"service '{name}' has a long-syntax mount without a target.")
            targets.append(target)
            continue
        if not isinstance(entry, str):
            raise _fail(application_id, "C5", f"service '{name}' has an unreadable mount entry {entry!r}.")
        fields = _split_outside_braces(entry)
        if len(fields) < 2:
            raise _fail(application_id, "C5", f"service '{name}' has a mount without a target: {entry!r}.")
        targets.append(fields[1])
    return targets


def _compose_container_ports(application_id: str, name: str, service: dict[str, Any]) -> list[int]:
    ports: list[int] = []
    for entry in service.get("ports") or []:
        if isinstance(entry, dict):
            target = entry.get("target")
            if not isinstance(target, int):
                raise _fail(application_id, "C7", f"service '{name}' has a long-syntax port without a target.")
            ports.append(target)
            continue
        fields = _split_outside_braces(str(entry))
        container = fields[-1].split("/", maxsplit=1)[0]
        if not container.isdigit():
            raise _fail(application_id, "C7", f"service '{name}' has an unreadable port entry {entry!r}.")
        ports.append(int(container))
    return ports


def _check_rejected_keys(application_id: str, compose: dict[str, Any], services: dict[str, Any]) -> None:
    for key in REJECTED_DOCUMENT_KEYS:
        if key in compose:
            raise _fail(application_id, "C3", f"the compose document declares a top-level '{key}' block.")
    networks = compose.get("networks")
    if isinstance(networks, dict):
        for network_name, definition in networks.items():
            if isinstance(definition, dict) and "driver" in definition:
                raise _fail(application_id, "C3", f"network '{network_name}' declares a driver.")
    for name, service in services.items():
        for key in REJECTED_SERVICE_KEYS:
            if key in service:
                raise _fail(application_id, "C3", f"service '{name}' declares '{key}'.")


def _check_images(application_id: str, services: dict[str, Any], overrides: dict[str, Any]) -> None:
    for name, service in services.items():
        override = overrides.get(name)
        if override is None:
            raise _fail(application_id, "C1", f"service '{name}' has no entry in manifest.overrides.json.")
        image = override.get("image")
        if "build" in service and not image:
            raise _fail(application_id, "C1", f"service '{name}' is built from source and declares no image override.")
        if not isinstance(image, str) or not DIGEST_PINNED_IMAGE.fullmatch(image):
            raise _fail(application_id, "C2", f"service '{name}' image is not pinned as '<ref>@sha256:<64 hex>'.")
        tag = override.get("imageTag")
        if not isinstance(tag, str) or not tag or tag == "latest":
            raise _fail(application_id, "C2", f"service '{name}' imageTag is missing or 'latest'.")


def _check_restart_policies(application_id: str, services: dict[str, Any]) -> None:
    for name, service in services.items():
        restart = service.get("restart", "no")
        if restart not in ACCEPTED_RESTART_POLICIES:
            raise _fail(application_id, "C6", f"service '{name}' declares restart '{restart}'.")


def _check_variable_buckets(application_id: str, variables: dict[str, Any], services: dict[str, Any]) -> None:
    exposed = [entry["name"] for entry in variables["exposed"]]
    fixed = list(variables["fixed"])
    dropped = [entry["name"] for entry in variables["dropped"]]
    classified = exposed + fixed + dropped
    duplicates = sorted({name for name in classified if classified.count(name) > 1})
    if duplicates:
        raise _fail(application_id, "C4", f"names classified more than once: {', '.join(duplicates)}.")
    known = set(classified)
    for name, service in services.items():
        for key in service_environment_keys(application_id, name, service):
            if key not in known:
                raise _fail(
                    application_id,
                    "C4",
                    f"service '{name}' passes '{key}', which no bucket in variables.json classifies.",
                )


def service_environment_keys(application_id: str, name: str, service: dict[str, Any]) -> list[str]:
    environment = service.get("environment") or []
    if isinstance(environment, dict):
        return list(environment)
    if not isinstance(environment, list):
        raise _fail(application_id, "C4", f"service '{name}' has an unreadable environment block.")
    keys: list[str] = []
    for entry in environment:
        if not isinstance(entry, str):
            raise _fail(application_id, "C4", f"service '{name}' has an unreadable environment entry {entry!r}.")
        keys.append(entry.split("=", maxsplit=1)[0])
    return keys


def _build_environment(
    application_id: str,
    name: str,
    service: dict[str, Any],
    variables: dict[str, Any],
) -> dict[str, str]:
    exposed = {entry["name"] for entry in variables["exposed"]}
    fixed = variables["fixed"]
    environment: dict[str, str] = {}
    for key in service_environment_keys(application_id, name, service):
        if key in exposed:
            environment[key] = f"${{{key}}}"
        elif key in fixed:
            environment[key] = str(fixed[key])
    return environment


def _build_ports(application_id: str, name: str, service: dict[str, Any], override: dict[str, Any]) -> list[dict]:
    declared = override.get("ports") or []
    ignored = override.get("ignoredPorts") or []
    for entry in ignored:
        if not entry.get("reason"):
            raise _fail(
                application_id, "C7", f"service '{name}' ignores port {entry.get('containerPort')} with no reason."
            )
    claimed = {entry["containerPort"] for entry in declared} | {entry["containerPort"] for entry in ignored}
    published = set(_compose_container_ports(application_id, name, service))
    unclaimed = sorted(published - claimed)
    if unclaimed:
        raise _fail(
            application_id,
            "C7",
            f"service '{name}' publishes container port(s) {unclaimed} claimed by neither ports[] nor ignoredPorts[].",
        )
    invented = sorted(claimed - published)
    if invented:
        raise _fail(
            application_id,
            "C7",
            f"service '{name}' claims container port(s) {invented} the compose does not publish.",
        )
    return [
        {
            "containerPort": _require_int(application_id, f"{name}.ports[].containerPort", entry["containerPort"]),
            "role": entry.get("role", "ui"),
            "preferredHostPort": _optional_int(
                application_id, f"{name}.ports[].preferredHostPort", entry.get("preferredHostPort")
            ),
            "openPath": entry.get("openPath"),
        }
        for entry in declared
    ]


def _build_storage_and_files(
    application_id: str,
    name: str,
    service: dict[str, Any],
    override: dict[str, Any],
    application_dir: Path,
) -> tuple[list[dict], list[dict]]:
    storage = [
        {"name": entry["name"], "containerPath": entry["containerPath"]} for entry in override.get("storage") or []
    ]
    files = [_inline_file(application_id, name, entry, application_dir) for entry in override.get("files") or []]
    ignored = {entry["containerPath"] for entry in override.get("ignoredMounts") or []}

    # Volumes the IMAGE declares (a Dockerfile VOLUME) that the compose does not mount. The converter runs
    # offline and cannot read an image, so the author has to name them: upstream leaving one anonymous is
    # invisible here, while the engine sees a real mount on the created container and refuses it as an
    # undeclared one. Each has to be a storage[] entry as well -- naming it here only exempts it from the
    # "the compose does not mount this" half of C5, it does not mount anything by itself.
    image_volumes = set(override.get("imageVolumes") or [])

    claimed = {entry["containerPath"] for entry in storage} | {entry["containerPath"] for entry in files} | ignored
    mounted = set(_compose_mount_targets(application_id, name, service))
    unclaimed = sorted(mounted - claimed)
    if unclaimed:
        raise _fail(
            application_id,
            "C5",
            f"service '{name}' mounts {unclaimed}, claimed by neither storage[], files[] nor ignoredMounts[].",
        )

    undeclared_image_volumes = sorted(image_volumes - {entry["containerPath"] for entry in storage})
    if undeclared_image_volumes:
        raise _fail(
            application_id,
            "C5",
            f"service '{name}' lists imageVolumes {undeclared_image_volumes} with no matching storage[] entry.",
        )

    invented = sorted(claimed - mounted - image_volumes)
    if invented:
        raise _fail(application_id, "C5", f"service '{name}' claims mount(s) {invented} the compose does not mount.")
    return storage, files


def _inline_file(application_id: str, name: str, entry: dict[str, Any], application_dir: Path) -> dict[str, Any]:
    source = entry["source"]
    root = application_dir.resolve()
    # resolve() follows symlinks, so is_relative_to rejects an absolute source, a '../' escape and a symlink
    # whose target sits outside the application folder in one check. The converter runs on an author's machine
    # against authored input; a source that leaves the folder is a mistake or an attempt to inline /etc/passwd.
    path = (root / source).resolve()
    if not path.is_relative_to(root):
        raise _fail(application_id, "files", f"service '{name}' file '{source}' resolves outside {root}.")

    # Sized before it is read: MAX_FILE_BYTES is the point of the limit, and reading first to measure would
    # defeat it on a file large enough to matter.
    size = path.stat().st_size
    if size > MAX_FILE_BYTES:
        raise _fail(
            application_id,
            "files",
            f"service '{name}' file '{source}' is {size} bytes, over the {MAX_FILE_BYTES}-byte limit.",
        )

    body = path.read_bytes()
    if len(body) > MAX_FILE_BYTES:
        raise _fail(
            application_id,
            "files",
            f"service '{name}' file '{source}' is {len(body)} bytes, over the {MAX_FILE_BYTES}-byte limit.",
        )
    return {
        "source": source,
        "containerPath": entry["containerPath"],
        "sha256": hashlib.sha256(body).hexdigest(),
        "contentBase64": base64.b64encode(body).decode("ascii"),
    }


def _build_healthcheck(application_id: str, service: dict[str, Any]) -> dict[str, Any] | None:
    healthcheck = service.get("healthcheck")
    if not healthcheck:
        return None
    test = healthcheck.get("test")
    if isinstance(test, str):
        test = ["CMD-SHELL", test]
    if not isinstance(test, list):
        raise _fail(application_id, "healthcheck", f"unreadable test {test!r}.")
    return {
        "test": [_unescape_dollars(str(element)) for element in test],
        "intervalSeconds": _parse_duration_seconds(
            application_id, "healthcheck.interval", healthcheck.get("interval", "30s")
        ),
        "timeoutSeconds": _parse_duration_seconds(
            application_id, "healthcheck.timeout", healthcheck.get("timeout", "30s")
        ),
        "retries": _require_int(application_id, "healthcheck.retries", healthcheck.get("retries", 3)),
        "startPeriodSeconds": _parse_duration_seconds(
            application_id, "healthcheck.start_period", healthcheck.get("start_period", "0s")
        ),
    }


def _build_depends_on(application_id: str, name: str, service: dict[str, Any]) -> list[dict[str, str]]:
    depends_on = service.get("depends_on") or []
    if isinstance(depends_on, list):
        return [{"service": str(target), "condition": "started"} for target in depends_on]
    if not isinstance(depends_on, dict):
        raise _fail(application_id, "C3", f"service '{name}' has an unreadable depends_on block.")
    edges: list[dict[str, str]] = []
    for target, definition in depends_on.items():
        condition = (definition or {}).get("condition", "service_started")
        if condition not in DEPENDS_ON_CONDITIONS:
            raise _fail(application_id, "C3", f"service '{name}' depends on '{target}' with condition '{condition}'.")
        edges.append({"service": str(target), "condition": DEPENDS_ON_CONDITIONS[condition]})
    return edges


def _build_extra_hosts(application_id: str, name: str, service: dict[str, Any]) -> list[str]:
    hosts: list[str] = []
    for entry in service.get("extra_hosts") or []:
        target = str(entry).split(":", maxsplit=1)[-1]
        if target != HOST_GATEWAY:
            raise _fail(application_id, "C3", f"service '{name}' declares extra_hosts entry '{entry}'.")
        if HOST_GATEWAY not in hosts:
            hosts.append(HOST_GATEWAY)
    return hosts


def _build_service(
    application_id: str,
    name: str,
    service: dict[str, Any],
    override: dict[str, Any],
    variables: dict[str, Any],
    application_dir: Path,
) -> dict[str, Any]:
    storage, files = _build_storage_and_files(application_id, name, service, override, application_dir)
    # capAdd comes from the overrides when the author declares one and from the compose otherwise:
    # upstream declares cap_add only for the service that needs a narrower set than Docker's default.
    cap_add = override.get("capAdd")
    if cap_add is None:
        cap_add = list(service.get("cap_add") or [])
    return {
        "name": name,
        "image": override["image"],
        "imageTag": override["imageTag"],
        "entrypoint": _argument_vector(application_id, name, "entrypoint", service.get("entrypoint")),
        "command": _argument_vector(application_id, name, "command", service.get("command")),
        "environment": _build_environment(application_id, name, service, variables),
        "ports": _build_ports(application_id, name, service, override),
        "storage": storage,
        "files": files,
        "healthcheck": _build_healthcheck(application_id, service),
        "dependsOn": _build_depends_on(application_id, name, service),
        "capAdd": list(cap_add),
        "extraHosts": _build_extra_hosts(application_id, name, service),
        "readOnlyRootFilesystem": bool(override.get("readOnlyRootFilesystem", False)),
    }


def _build_variables(application_id: str, variables: dict[str, Any]) -> list[dict[str, Any]]:
    declared: list[dict[str, Any]] = []
    for entry in variables["exposed"]:
        validation = entry.get("validation")
        path = f"variables[{entry['name']}].validation"
        declared.append(
            {
                "name": entry["name"],
                "label": entry["label"],
                "description": entry.get("description"),
                "type": entry["type"],
                "required": bool(entry["required"]),
                "default": entry.get("default"),
                "allowedValues": entry.get("allowedValues"),
                "validation": None
                if validation is None
                else {
                    "minLength": _optional_int(application_id, f"{path}.minLength", validation.get("minLength")),
                    "maxLength": _optional_int(application_id, f"{path}.maxLength", validation.get("maxLength")),
                    "pattern": validation.get("pattern"),
                },
                "advanced": bool(entry.get("advanced", False)),
            }
        )
    return declared


def build_application(application_dir: Path) -> dict[str, Any]:
    """Build one application manifest from its authoring folder, running the C1..C7 gates."""
    application_id = application_dir.name
    overrides = json.loads((application_dir / "manifest.overrides.json").read_text(encoding="utf-8"))
    variables = json.loads((application_dir / "variables.json").read_text(encoding="utf-8"))
    compose = yaml.safe_load((application_dir / "compose.yml").read_text(encoding="utf-8"))

    services = compose.get("services") or {}
    service_overrides = overrides["services"]
    metadata = overrides["application"]
    if metadata["id"] != application_id:
        raise _fail(application_id, "metadata", f"id '{metadata['id']}' does not match the folder name.")

    _check_rejected_keys(application_id, compose, services)
    _check_images(application_id, services, service_overrides)
    _check_restart_policies(application_id, services)
    _check_variable_buckets(application_id, variables, services)

    manifest: dict[str, Any] = {
        "id": metadata["id"],
        "manifestVersion": _require_int(application_id, "application.manifestVersion", metadata["manifestVersion"]),
        HASH_KEY: "",
        "displayName": metadata["displayName"],
        "summary": metadata["summary"],
        "description": metadata["description"],
        "homepage": metadata["homepage"],
        "license": metadata["license"],
        "trust": metadata["trust"],
        "testedVersion": metadata["testedVersion"],
        "requires": list(metadata["requires"]),
        "permissions": {
            "internet": bool(metadata["permissions"]["internet"]),
            "localNetwork": bool(metadata["permissions"]["localNetwork"]),
            "hostFiles": metadata["permissions"]["hostFiles"],
            "gpu": metadata["permissions"]["gpu"],
        },
        "resources": {
            field: _require_int(application_id, f"application.resources.{field}", metadata["resources"][field])
            for field in ("minimumMemoryMb", "recommendedMemoryMb", "cpuHint", "pidsLimit")
        },
        "services": [
            _build_service(application_id, name, service, service_overrides[name], variables, application_dir)
            for name, service in services.items()
        ],
        "variables": _build_variables(application_id, variables),
    }
    manifest[HASH_KEY] = manifest_fingerprint(manifest)
    return manifest


def check_version_bumps(applications: list[dict[str, Any]], committed: str | None) -> None:
    """Refuse a manifest whose fingerprint moved while its ``manifestVersion`` stood still.

    The version is what reaches an instance that is already installed: ``ExternalAppService.UpdateAsync``
    treats a target whose ``manifestVersion`` is not greater than the stored one as a no-op, so a manifest
    edited without a bump changes fresh installs only -- every existing instance keeps running on its stored
    snapshot and nothing surfaces the drift. A capability narrowing shipped that way is half a fix.

    ``committed`` is the document as committed at ``HEAD``, not the one on disk, so rebuilding several times
    while authoring one change never asks for a second bump. ``None`` when there is no baseline to compare
    against, and an application absent from it is new.
    """
    if committed is None:
        return

    baseline = {application["id"]: application for application in json.loads(committed).get("applications", [])}
    for application in applications:
        previous = baseline.get(application["id"])
        if previous is None or previous[HASH_KEY] == application[HASH_KEY]:
            continue

        if application["manifestVersion"] <= previous["manifestVersion"]:
            raise _fail(
                application["id"],
                "manifestVersion",
                f"the manifest changed ({previous[HASH_KEY][:12]} -> {application[HASH_KEY][:12]}) while "
                f"manifestVersion stayed at {previous['manifestVersion']}. Bump it in manifest.overrides.json, "
                "or no instance that is already installed will ever receive the change.",
            )


def committed_document() -> str | None:
    """The dist document as committed at ``HEAD``, or ``None`` when there is none to read.

    ``None`` covers a checkout that is not a git repository and a document that is not in ``HEAD`` yet; both
    mean there is no baseline for :func:`check_version_bumps`, not that the rule passed.
    """
    read = subprocess.run(  # noqa: S603
        ["git", "-C", str(REPO_ROOT), "show", f"HEAD:{DIST_PATH.relative_to(REPO_ROOT).as_posix()}"],  # noqa: S607
        capture_output=True,
        text=True,
        check=False,
    )
    return read.stdout if read.returncode == 0 else None


def build_document() -> dict[str, Any]:
    """Build the whole catalog document. Always every application: both outputs are whole-catalog files.

    A catalog with no applications builds the empty document rather than raising: that is what ships today, and
    an absent ``applications/`` directory is what a checkout of an empty catalog carries, because git records no
    empty directory.
    """
    entries = APPLICATIONS_DIR.iterdir() if APPLICATIONS_DIR.is_dir() else []
    directories = sorted(entry for entry in entries if entry.is_dir())
    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAtUtc": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "applications": [build_application(directory) for directory in directories],
    }


def serialize(document: dict[str, Any]) -> str:
    """Serialise the document exactly as it is committed: sorted keys, two-space indent, trailing newline."""
    return json.dumps(document, indent=2, ensure_ascii=False, sort_keys=True) + "\n"


def _display(path: Path) -> str:
    """The repo-relative path when there is one; ``--check`` is also run against a redirected path in tests."""
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def _check(document: dict[str, Any]) -> int:
    if not DIST_PATH.exists():
        print(f"{DIST_PATH} does not exist; run the build without --check first.", file=sys.stderr)
        return 1

    # Raw text against raw text, and both outputs, not a reparsed object against a reparsed object: the committed
    # files are byte artefacts. Comparing parsed documents passes a hand-reindented file, a reordered key or a
    # stripped trailing newline, and the seed -- which no comparison opened at all -- could differ from dist.
    document["generatedAtUtc"] = json.loads(DIST_PATH.read_text(encoding="utf-8")).get(
        "generatedAtUtc", document["generatedAtUtc"]
    )
    built_text = serialize(document)

    status = 0
    for path in (DIST_PATH, SEED_PATH):
        if not path.exists():
            print(f"{path} does not exist; run the build without --check first.", file=sys.stderr)
            status = 1
            continue
        committed_text = path.read_text(encoding="utf-8")
        if committed_text == built_text:
            print(f"{_display(path)} is up to date.")
            continue
        diff = difflib.unified_diff(
            committed_text.splitlines(keepends=True),
            built_text.splitlines(keepends=True),
            fromfile=f"committed {path.name}",
            tofile="rebuilt",
        )
        sys.stdout.writelines(diff)
        print(f"\n{_display(path)} is stale; re-run the build and commit the result.", file=sys.stderr)
        status = 1
    return status


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build the External Apps catalog document.")
    parser.add_argument(
        "--check", action="store_true", help="verify the committed document and the embedded seed are current"
    )
    arguments = parser.parse_args(argv)

    try:
        document = build_document()
        check_version_bumps(document["applications"], committed_document())
    except CatalogBuildError as error:
        print(f"catalog build failed: {error}", file=sys.stderr)
        return 1

    if arguments.check:
        return _check(document)

    text = serialize(document)
    DIST_PATH.parent.mkdir(parents=True, exist_ok=True)
    DIST_PATH.write_text(text, encoding="utf-8")
    SEED_PATH.write_text(text, encoding="utf-8")
    print(f"wrote {DIST_PATH.relative_to(REPO_ROOT)} and {SEED_PATH.relative_to(REPO_ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
