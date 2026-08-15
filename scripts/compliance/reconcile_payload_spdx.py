#!/usr/bin/env python3
"""Replace build-tree package guesses with exact published runtime components."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import quote

from bundle_input_evidence import load_bundle_packages

INVALID_LICENSES = {"", "UNKNOWN", "NOASSERTION"}
THREE_PART_VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")


def load_json(path: Path) -> dict:
    with path.open(encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def require_license(value: object, label: str) -> str:
    if not isinstance(value, str) or value.strip().upper() in INVALID_LICENSES:
        raise ValueError(f"{label} has no approved license expression")
    return value.strip()


def package_id(purl: str) -> str:
    return f"SPDXRef-Package-{hashlib.sha256(purl.encode()).hexdigest().upper()}"


def package(name: str, version: str, license_expression: str, purl: str) -> dict:
    return {
        "name": name,
        "SPDXID": package_id(purl),
        "downloadLocation": "NOASSERTION",
        "filesAnalyzed": False,
        "licenseConcluded": license_expression,
        "licenseDeclared": license_expression,
        "copyrightText": "NOASSERTION",
        "versionInfo": version,
        "externalRefs": [
            {
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": purl,
            }
        ],
        "supplier": "NOASSERTION",
    }


def backend_license_map(
    backend_manifest: dict, runtime_identifier: str, bundle_input_manifest: Path
) -> dict[tuple[str, str], str]:
    if backend_manifest.get("runtimeIdentifier") != runtime_identifier:
        raise ValueError("backend license inventory runtime identifier does not match the payload")
    shipment_evidence = backend_manifest.get("shipmentEvidence")
    expected_hash = hashlib.sha256(bundle_input_manifest.read_bytes()).hexdigest()
    if not isinstance(shipment_evidence, dict) or shipment_evidence.get("sha256") != expected_hash:
        raise ValueError("backend license inventory is not bound to the supplied bundle-input evidence")
    result: dict[tuple[str, str], str] = {}
    entries = backend_manifest.get("packages")
    if not isinstance(entries, list):
        raise ValueError("backend license inventory has no packages array")
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("backend license inventory contains an invalid package entry")
        name = entry.get("name")
        version = entry.get("version")
        if not isinstance(name, str) or not isinstance(version, str):
            raise ValueError("backend license inventory contains an invalid identity")
        license_files = entry.get("licenseFiles")
        if not isinstance(license_files, list) or not license_files:
            raise ValueError(f"backend package {name}/{version} has no bundled legal terms")
        key = (name.casefold(), version)
        value = require_license(entry.get("licenseExpression"), f"backend package {name}/{version}")
        if key in result and result[key] != value:
            raise ValueError(f"backend package {name}/{version} has conflicting license expressions")
        result[key] = value
    return result


def runtime_license(name: str, runtime_identifier: str) -> str:
    lowered = name.casefold()
    if lowered.startswith(("runtimepack.microsoft.aspnetcore.app.runtime.", "microsoft.aspnetcore.app.runtime.")):
        if runtime_identifier == "win-x64":
            raise ValueError("Windows framework-dependent payload must not include a .NET runtime pack")
        return "MIT"
    if lowered.startswith(("runtimepack.microsoft.netcore.app.runtime.", "microsoft.netcore.app.runtime.")):
        if runtime_identifier == "win-x64":
            raise ValueError("Windows framework-dependent payload must not include a .NET runtime pack")
        return "MIT"
    raise ValueError(f"unrecognized runtime pack {name}")


def require_legal_document(path: Path | None, label: str) -> Path:
    if path is None or not path.is_file() or path.stat().st_size == 0:
        raise ValueError(f"Windows framework-dependent payload requires the {label}")
    return path


def windows_apphost_component(
    runtime_identifier: str,
    version: str | None,
    license_path: Path | None,
    notices_path: Path | None,
) -> dict | None:
    supplied = any(value is not None for value in (version, license_path, notices_path))
    if runtime_identifier != "win-x64":
        if supplied:
            raise ValueError("Windows apphost metadata is only valid for the win-x64 payload")
        return None
    if version is None or THREE_PART_VERSION.fullmatch(version) is None:
        raise ValueError("Windows framework-dependent payload requires the Windows apphost version")
    exact_license = require_legal_document(license_path, "Windows apphost MIT license")
    exact_notices = require_legal_document(notices_path, "Windows apphost third-party notices")
    name = "Microsoft.NETCore.App.Host.win-x64"
    purl = f"pkg:nuget/{name}@{version}"
    component = package(name, version, "MIT", purl)
    component["licenseComments"] = (
        "Exact host-pack terms bundled at licenses/dotnet/DOTNET-APPHOST-LICENSE.txt "
        f"(SHA-256 {hashlib.sha256(exact_license.read_bytes()).hexdigest()}) and "
        "licenses/dotnet/DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt "
        f"(SHA-256 {hashlib.sha256(exact_notices.read_bytes()).hexdigest()})."
    )
    return component


def detected_backend_packages(
    deps: dict,
    bundle_input_manifest: Path,
    runtime_identifier: str,
    licenses: dict[tuple[str, str], str],
) -> tuple[list[dict], list[dict]]:
    matching_targets = [key for key in deps.get("targets", {}) if key.endswith(f"/{runtime_identifier}")]
    if len(matching_targets) != 1:
        raise ValueError(f"expected one {runtime_identifier} target in deps.json, found {len(matching_targets)}")
    libraries = deps.get("libraries", {})
    deps_package_keys: set[tuple[str, str]] = set()
    for identity, library in libraries.items():
        if not isinstance(identity, str) or not isinstance(library, dict) or library.get("type") == "project":
            continue
        name, separator, version = identity.rpartition("/")
        if not separator or not name or not version:
            raise ValueError(f"invalid deps.json package identity: {identity}")
        deps_package_keys.add((name.casefold(), version))
    packages: list[dict] = []
    extracted_licenses: list[dict] = []
    detected_inventory_keys: set[tuple[str, str]] = set()
    bundle_packages = load_bundle_packages(bundle_input_manifest, runtime_identifier)
    for (folded_name, version), evidence in sorted(bundle_packages.items()):
        name = evidence["name"]
        if folded_name.startswith(
            (
                "runtimepack.microsoft.",
                "microsoft.netcore.app.runtime.",
                "microsoft.aspnetcore.app.runtime.",
            )
        ):
            expression = runtime_license(name, runtime_identifier)
        else:
            inventory_key = (folded_name, version)
            if inventory_key not in deps_package_keys:
                raise ValueError(f"bundle input package {name}/{version} is absent from RID deps.json libraries")
            try:
                expression = licenses[inventory_key]
            except KeyError as error:
                raise ValueError(
                    f"shipped backend package {name}/{version} has no approved license inventory entry"
                ) from error
            detected_inventory_keys.add(inventory_key)
        purl = f"pkg:nuget/{quote(name, safe='._-')}@{quote(version, safe='._-')}"
        packages.append(package(name, version, expression, purl))
    stale = set(licenses) - detected_inventory_keys
    if stale:
        formatted = [f"{name}/{version}" for name, version in sorted(stale)]
        raise ValueError(f"stale backend license inventory entries: {formatted}")
    return packages, extracted_licenses


def detected_frontend_packages(manifest: dict) -> list[dict]:
    result: list[dict] = []
    components = manifest.get("components")
    if not isinstance(components, list) or not components:
        raise ValueError("frontend component manifest has no detected components")
    for entry in components:
        if not isinstance(entry, dict):
            raise ValueError("frontend component manifest contains an invalid entry")
        name = entry.get("name")
        version = entry.get("version")
        purl = entry.get("purl")
        if not (
            isinstance(name, str) and name and isinstance(version, str) and version and isinstance(purl, str) and purl
        ):
            raise ValueError("frontend component manifest contains an incomplete identity")
        result.append(
            package(name, version, require_license(entry.get("license"), f"frontend package {name}/{version}"), purl)
        )
    return result


def reconcile(
    spdx_path: Path,
    deps_path: Path,
    bundle_input_manifest: Path,
    backend_manifest_path: Path,
    frontend_manifest_path: Path,
    runtime_identifier: str,
    legacy_library_license_path: Path | None = None,
    *,
    windows_apphost_version: str | None = None,
    windows_apphost_license_path: Path | None = None,
    windows_apphost_notices_path: Path | None = None,
) -> tuple[int, int]:
    if legacy_library_license_path is not None:
        raise ValueError("the Windows framework-dependent payload must not use the .NET Library License")
    document = load_json(spdx_path)
    root = next((entry for entry in document.get("packages", []) if entry.get("SPDXID") == "SPDXRef-RootPackage"), None)
    if root is None:
        raise ValueError("generated SPDX document has no root package")
    root["licenseConcluded"] = "Apache-2.0"
    root["licenseDeclared"] = "Apache-2.0"
    root["copyrightText"] = "Copyright 2026 w0rldx"

    backend, extracted = detected_backend_packages(
        load_json(deps_path),
        bundle_input_manifest,
        runtime_identifier,
        backend_license_map(load_json(backend_manifest_path), runtime_identifier, bundle_input_manifest),
    )
    frontend = detected_frontend_packages(load_json(frontend_manifest_path))
    apphost = windows_apphost_component(
        runtime_identifier,
        windows_apphost_version,
        windows_apphost_license_path,
        windows_apphost_notices_path,
    )
    if apphost is not None:
        backend.append(apphost)
    components = backend + frontend
    purls = [entry["externalRefs"][0]["referenceLocator"] for entry in components]
    if len(purls) != len(set(purls)):
        raise ValueError("detected shipped component purls are not unique")

    document["packages"] = [root, *sorted(components, key=lambda entry: entry["externalRefs"][0]["referenceLocator"])]
    document["relationships"] = [
        {
            "relationshipType": "DESCRIBES",
            "relatedSpdxElement": "SPDXRef-RootPackage",
            "spdxElementId": "SPDXRef-DOCUMENT",
        },
        *[
            {
                "relationshipType": "DEPENDS_ON",
                "relatedSpdxElement": entry["SPDXID"],
                "spdxElementId": "SPDXRef-RootPackage",
            }
            for entry in document["packages"][1:]
        ],
    ]
    document.pop("externalDocumentRefs", None)
    if extracted:
        unique = {entry["licenseId"]: entry for entry in extracted}
        document["hasExtractedLicensingInfos"] = list(unique.values())
    else:
        document.pop("hasExtractedLicensingInfos", None)
    document["documentComment"] = (
        "File hashes were generated by Microsoft.Sbom.DotNetTool 4.1.5. Package relationships were reconciled "
        "against hashed MSBuild FilesToBundle plus loose ResolvedFileToPublish evidence, the RID-specific .deps.json, "
        "and the production frontend component manifest. "
        "License expressions come from the exact per-RID backend and frontend compliance inventories and are review "
        "evidence, not legal certification."
    )

    encoded = (json.dumps(document, indent=2, sort_keys=True) + "\n").encode()
    spdx_path.write_bytes(encoded)
    spdx_path.with_name(f"{spdx_path.name}.sha256").write_text(hashlib.sha256(encoded).hexdigest(), encoding="ascii")
    return len(backend), len(frontend)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", choices=("linux-x64", "win-x64"), required=True)
    parser.add_argument("--spdx", type=Path, required=True)
    parser.add_argument("--deps-json", type=Path, required=True)
    parser.add_argument("--bundle-input-manifest", type=Path, required=True)
    parser.add_argument("--backend-manifest", type=Path, required=True)
    parser.add_argument("--frontend-manifest", type=Path, required=True)
    parser.add_argument("--windows-apphost-version")
    parser.add_argument("--windows-apphost-license", type=Path)
    parser.add_argument("--windows-apphost-notices", type=Path)
    args = parser.parse_args()
    backend_count, frontend_count = reconcile(
        args.spdx,
        args.deps_json,
        args.bundle_input_manifest,
        args.backend_manifest,
        args.frontend_manifest,
        args.rid,
        None,
        windows_apphost_version=args.windows_apphost_version,
        windows_apphost_license_path=args.windows_apphost_license,
        windows_apphost_notices_path=args.windows_apphost_notices,
    )
    print(f"reconciled payload SPDX: {backend_count} backend + {frontend_count} frontend shipped components")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
