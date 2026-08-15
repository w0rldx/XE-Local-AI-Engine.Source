#!/usr/bin/env python3
"""Generate one RID payload's exact backend NuGet license and notice corpus."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path
from urllib.parse import quote

from bundle_input_evidence import load_bundle_packages

NUGET_LICENSE_VERSION = "4.0.14"
INVALID_LICENSES = {"", "UNKNOWN", "NOASSERTION"}
TERM_FILE_PATTERN = re.compile(
    r"^(?:(?:licen[cs]e|notice|copyright)s?|copying)(?:$|[-_. ])"
    r"|^third[-_. ]?party(?:[-_. ]?notices?)?(?:$|[-_. ])",
    re.IGNORECASE,
)
STANDARD_LICENSE_TEXTS = {
    "MIT": (Path("nuget/standard/MIT.txt"), Path("nuget/standard/MIT.source.txt")),
    "Apache-2.0": (Path("nuget/standard/Apache-2.0.txt"), Path("nuget/standard/Apache-2.0.source.txt")),
    "BSD-3-Clause": (
        Path("nuget/standard/BSD-3-Clause.txt"),
        Path("nuget/standard/BSD-3-Clause.source.txt"),
    ),
    "ISC": (Path("nuget/standard/ISC.txt"), Path("nuget/standard/ISC.source.txt")),
}
SPECIAL_LICENSE_TEXTS = {
    ("sqlitepclraw.lib.e_sqlite3", "3.50.3", "blessing"): (
        Path("nuget/SQLite-3.50.3-public-domain.html"),
        Path("nuget/SQLite-3.50.3-public-domain.html.source.txt"),
    ),
    ("utf.unknown", "2.6.0", "MPL-1.1"): (
        Path("nuget/UTF.Unknown-2.6.0-MPL-1.1.txt"),
        Path("nuget/UTF.Unknown-2.6.0-MPL-1.1.txt.source.txt"),
    ),
}
UTF_UNKNOWN_SOURCE_COMMIT = "7e69ebbdd6ef96a3625fcaf39df42429b8eb0463"
SPECIAL_SOURCE_AVAILABILITY = {
    ("utf.unknown", "2.6.0", "MPL-1.1"): {
        "licenseBasis": "MPL-1.1",
        "notice": (
            Path("nuget/UTF.Unknown-2.6.0-SOURCE-AVAILABILITY.txt"),
            Path("nuget/UTF.Unknown-2.6.0-SOURCE-AVAILABILITY.txt.source.txt"),
        ),
        "sourceArchive": (f"https://github.com/CharsetDetector/UTF-unknown/archive/{UTF_UNKNOWN_SOURCE_COMMIT}.tar.gz"),
        "sourceCommit": UTF_UNKNOWN_SOURCE_COMMIT,
        "sourceRepository": "https://github.com/CharsetDetector/UTF-unknown",
        "upstreamTag": "v2.6",
    }
}
UPSTREAM_PACKAGE_LICENSE_TEXTS = {
    **{
        (name.casefold(), "8.2.0", "MIT"): (
            Path("nuget/upstream/FastEndpoints-8.2.0-LICENSE.md"),
            Path("nuget/upstream/FastEndpoints-8.2.0-LICENSE.md.source.txt"),
        )
        for name in (
            "FastEndpoints",
            "FastEndpoints.Attributes",
            "FastEndpoints.Core",
            "FastEndpoints.JobQueues",
            "FastEndpoints.Messaging",
            "FastEndpoints.Messaging.Core",
            "FastEndpoints.Swagger",
        )
    },
    ("scalar.aspnetcore", "2.16.10", "MIT"): (
        Path("nuget/upstream/Scalar.AspNetCore-2.16.10-LICENSE"),
        Path("nuget/upstream/Scalar.AspNetCore-2.16.10-LICENSE.source.txt"),
    ),
    ("scrutor", "7.0.0", "MIT"): (
        Path("nuget/upstream/Scrutor-7.0.0-LICENSE"),
        Path("nuget/upstream/Scrutor-7.0.0-LICENSE.source.txt"),
    ),
    ("timezoneconverter", "7.0.0", "MIT"): (
        Path("nuget/upstream/TimeZoneConverter-7.0.0-LICENSE.txt"),
        Path("nuget/upstream/TimeZoneConverter-7.0.0-LICENSE.txt.source.txt"),
    ),
}


def load_json(path: Path) -> object:
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def required_text(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{label} is missing")
    return value.strip()


def pinned_tool_version(manifest_path: Path) -> str:
    manifest = load_json(manifest_path)
    if not isinstance(manifest, dict):
        raise ValueError(f"{manifest_path} must contain a JSON object")
    tools = manifest.get("tools")
    entry = tools.get("nuget-license") if isinstance(tools, dict) else None
    version = entry.get("version") if isinstance(entry, dict) else None
    if version != NUGET_LICENSE_VERSION:
        raise ValueError(f"expected pinned nuget-license {NUGET_LICENSE_VERSION} in {manifest_path}, found {version!r}")
    return version


def is_runtime_pack(name: str) -> bool:
    folded = name.casefold()
    return folded.startswith("runtimepack.microsoft.") or folded.startswith(
        ("microsoft.netcore.app.runtime.", "microsoft.aspnetcore.app.runtime.")
    )


def shipped_packages(rid: str, deps_path: Path, bundle_input_manifest: Path) -> dict[tuple[str, str], dict]:
    document = load_json(deps_path)
    if not isinstance(document, dict):
        raise ValueError(f"{deps_path} must contain a JSON object")
    targets = document.get("targets")
    libraries = document.get("libraries")
    if not isinstance(targets, dict) or not isinstance(libraries, dict):
        raise ValueError(f"{deps_path} has no valid targets/libraries objects")
    matching = [key for key in targets if key.endswith(f"/{rid}")]
    if len(matching) != 1:
        raise ValueError(f"expected one {rid} target in {deps_path}, found {len(matching)}")
    if not isinstance(targets[matching[0]], dict):
        raise ValueError(f"{deps_path} has an invalid {rid} target")
    deps_packages: dict[tuple[str, str], dict] = {}
    for identity, library in libraries.items():
        if not isinstance(identity, str) or not isinstance(library, dict) or library.get("type") == "project":
            continue
        name, separator, version = identity.rpartition("/")
        if not separator or not name or not version:
            raise ValueError(f"invalid deps.json package identity: {identity}")
        if is_runtime_pack(name):
            continue
        package_path = required_text(library.get("path"), f"deps.json library {identity} path")
        key = (name.casefold(), version)
        if key in deps_packages:
            raise ValueError(f"duplicate deps.json package identity {identity}")
        deps_packages[key] = {"name": name, "packagePath": package_path}

    selected: dict[tuple[str, str], dict] = {}
    for key, evidence in load_bundle_packages(bundle_input_manifest, rid).items():
        if is_runtime_pack(evidence["name"]):
            continue
        try:
            package = deps_packages[key]
        except KeyError as error:
            raise ValueError(
                f"bundle input package {evidence['name']}/{key[1]} is absent from the RID deps.json libraries"
            ) from error
        selected[key] = {
            "bundleInputs": evidence["inputs"],
            "name": package["name"],
            "packagePath": package["packagePath"],
        }
    return selected


def metadata_index(metadata_path: Path) -> tuple[dict[tuple[str, str], dict], dict[str, set[str]]]:
    document = load_json(metadata_path)
    if not isinstance(document, list):
        raise ValueError(f"{metadata_path} must contain the JSON array emitted by nuget-license")
    exact: dict[tuple[str, str], dict] = {}
    versions_by_name: dict[str, set[str]] = {}
    for index, entry in enumerate(document):
        if not isinstance(entry, dict):
            raise ValueError(f"nuget-license metadata entry {index} is not an object")
        name = required_text(entry.get("PackageId"), f"nuget-license metadata entry {index} PackageId")
        version = required_text(entry.get("PackageVersion"), f"nuget-license metadata entry {index} PackageVersion")
        key = (name.casefold(), version)
        if key in exact:
            raise ValueError(f"duplicate nuget-license metadata identity {name}/{version}")
        exact[key] = entry
        versions_by_name.setdefault(name.casefold(), set()).add(version)
    return exact, versions_by_name


def verify_curated_text(source: Path, provenance: Path) -> bytes:
    if not source.is_file() or source.stat().st_size == 0:
        raise ValueError(f"curated license text is missing or empty: {source}")
    if not provenance.is_file():
        raise ValueError(f"curated license provenance is missing: {provenance}")
    expected_hashes = [
        line.removeprefix("SHA-256:").strip()
        for line in provenance.read_text(encoding="utf-8").splitlines()
        if line.startswith("SHA-256:")
    ]
    if len(expected_hashes) != 1 or len(expected_hashes[0]) != 64:
        raise ValueError(f"curated license provenance has no unique SHA-256: {provenance}")
    contents = source.read_bytes()
    actual = hashlib.sha256(contents).hexdigest()
    if actual != expected_hashes[0].casefold():
        raise ValueError(f"curated license text SHA-256 mismatch: {source}")
    return contents


def license_mapping(name: str, version: str, expression: str, copyright_text: str) -> tuple[Path, Path]:
    exact_key = (name.casefold(), version, expression)
    if exact_key in UPSTREAM_PACKAGE_LICENSE_TEXTS:
        return UPSTREAM_PACKAGE_LICENSE_TEXTS[exact_key]
    if expression in {"blessing", "MPL-1.1"}:
        try:
            return SPECIAL_LICENSE_TEXTS[exact_key]
        except KeyError as error:
            raise ValueError(
                f"package {name}/{version} has unsupported special license mapping: {expression}"
            ) from error
    if expression in STANDARD_LICENSE_TEXTS:
        if expression == "ISC":
            raise ValueError(
                f"package {name}/{version} with ISC requires an exact package-owned or reviewed upstream license"
            )
        if not copyright_text and expression in {"MIT", "BSD-3-Clause"}:
            raise ValueError(
                f"package {name}/{version} has author-only {expression} metadata and no exact upstream license mapping"
            )
        return STANDARD_LICENSE_TEXTS[expression]
    raise ValueError(f"package {name}/{version} has unsupported license expression: {expression}")


def render_attributed_standard_fallback(
    name: str,
    version: str,
    expression: str,
    copyright_text: str,
    contents: bytes,
) -> bytes:
    markers = {
        "MIT": "Copyright (c) <year> <copyright holders>",
        "BSD-3-Clause": "Copyright (c) <year> <owner>. ",
    }
    marker = markers.get(expression)
    if marker is None:
        return contents
    if not copyright_text or "\n" in copyright_text or "\r" in copyright_text:
        raise ValueError(f"package {name}/{version} has invalid {expression} copyright metadata")
    text = contents.decode("utf-8")
    if text.count(marker) != 1:
        raise ValueError(f"curated {expression} fallback has no unique attribution placeholder")
    rendered = text.replace(marker, copyright_text)
    if any(placeholder in rendered for placeholder in ("<year>", "<copyright holders>", "<owner>")):
        raise ValueError(f"curated {expression} fallback retained an attribution placeholder")
    return rendered.encode("utf-8")


def term_role(filename: str) -> str:
    folded = filename.casefold()
    if folded.startswith(("notice", "third-party", "third_party", "thirdparty")):
        return "notice"
    if folded.startswith("copyright"):
        return "copyright"
    return "license"


def discover_package_terms(package_root: Path) -> list[Path]:
    if not package_root.is_dir():
        raise ValueError(f"restored NuGet package root is missing: {package_root}")
    candidates = [
        path
        for path in package_root.iterdir()
        if path.is_file() and TERM_FILE_PATTERN.match(path.name) and ".source." not in path.name.casefold()
    ]
    candidates.sort(key=lambda path: (path.name.casefold(), path.name))
    folded_names = [path.name.casefold() for path in candidates]
    if len(folded_names) != len(set(folded_names)):
        raise ValueError(f"package term filenames collide case-insensitively in {package_root}")
    for path in candidates:
        if path.stat().st_size == 0:
            raise ValueError(f"package-owned legal term is empty: {path}")
    return candidates


def write_verified(source: Path, destination: Path, expected_hash: str, label: str) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    if not destination.is_file() or hashlib.sha256(destination.read_bytes()).hexdigest() != expected_hash:
        raise ValueError(f"omitted expected package term {label}")


def package_output_directory(output_directory: Path, name: str, version: str) -> tuple[Path, str]:
    segment = f"{quote(name, safe='._-')}@{quote(version, safe='._-')}"
    relative = f"licenses/nuget/packages/{segment}"
    return output_directory / relative, relative


def build_component(
    rid: str,
    selection: dict,
    version: str,
    metadata: dict,
    license_root: Path,
    packages_root: Path,
    output_directory: Path,
) -> dict:
    name = required_text(metadata.get("PackageId"), f"package {selection['name']}/{version} PackageId")
    expression = required_text(metadata.get("License"), f"package {name}/{version} License")
    if expression.upper() in INVALID_LICENSES:
        raise ValueError(f"package {name}/{version} has unreviewable license metadata: {expression}")
    if expression not in {*STANDARD_LICENSE_TEXTS, "blessing", "MPL-1.1"}:
        raise ValueError(f"package {name}/{version} has unsupported license expression: {expression}")
    authors_value = metadata.get("Authors")
    copyright_value = metadata.get("Copyright")
    authors = authors_value.strip() if isinstance(authors_value, str) else ""
    copyright_text = copyright_value.strip() if isinstance(copyright_value, str) else ""
    if not authors and not copyright_text:
        raise ValueError(f"package {name}/{version} is missing attribution: Authors and Copyright are blank")

    package_root = packages_root / selection["packagePath"]
    discovered = discover_package_terms(package_root)
    package_output, relative_output = package_output_directory(output_directory, name, version)
    files: list[dict] = []
    for source in discovered:
        contents = source.read_bytes()
        digest = hashlib.sha256(contents).hexdigest()
        destination = package_output / source.name
        write_verified(source, destination, digest, source.name)
        files.append(
            {
                "path": f"{relative_output}/{source.name}",
                "role": term_role(source.name),
                "sha256": digest,
                "source": "package",
                "sourceFile": source.name,
            }
        )

    if not any(entry["role"] == "license" for entry in files):
        relative_fallback, relative_provenance = license_mapping(name, version, expression, copyright_text)
        source = license_root / relative_fallback
        contents = verify_curated_text(source, license_root / relative_provenance)
        if relative_fallback == STANDARD_LICENSE_TEXTS.get(expression, (None, None))[0]:
            contents = render_attributed_standard_fallback(name, version, expression, copyright_text, contents)
        destination = package_output / source.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(contents)
        digest = hashlib.sha256(contents).hexdigest()
        if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
            raise ValueError(f"omitted expected curated fallback {source.name}")
        files.append(
            {
                "path": f"{relative_output}/{source.name}",
                "repositorySource": f"third-party/{relative_fallback.as_posix()}",
                "role": "license",
                "sha256": digest,
                "source": "curated-fallback",
                "sourceFile": source.name,
            }
        )

    source_availability = SPECIAL_SOURCE_AVAILABILITY.get((name.casefold(), version, expression))
    if source_availability is not None:
        notice_path, provenance_path = source_availability["notice"]
        contents = verify_curated_text(license_root / notice_path, license_root / provenance_path)
        destination = package_output / notice_path.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(contents)
        digest = hashlib.sha256(contents).hexdigest()
        if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
            raise ValueError(f"omitted expected source-availability notice {notice_path.name}")
        files.append(
            {
                "path": f"{relative_output}/{notice_path.name}",
                "repositorySource": f"third-party/{notice_path.as_posix()}",
                "role": "notice",
                "sha256": digest,
                "source": "curated-source-availability",
                "sourceFile": notice_path.name,
            }
        )

    files.sort(key=lambda entry: (entry["role"] != "license", entry["sourceFile"].casefold(), entry["sourceFile"]))
    component = {
        "attributionPolicy": "NuGet Authors are preserved as authors and are not inferred to be copyright owners.",
        "authors": authors or None,
        "bundleInputs": selection["bundleInputs"],
        "copyright": copyright_text or None,
        "licenseExpression": expression,
        "licenseFiles": files,
        "licenseTextPath": next(entry["path"] for entry in files if entry["role"] == "license"),
        "licenseUrl": metadata.get("LicenseUrl") if isinstance(metadata.get("LicenseUrl"), str) else None,
        "name": name,
        "projectUrl": metadata.get("PackageProjectUrl") if isinstance(metadata.get("PackageProjectUrl"), str) else None,
        "runtimeIdentifier": rid,
        "version": version,
    }
    if source_availability is not None:
        component["sourceAvailability"] = {key: value for key, value in source_availability.items() if key != "notice"}
    return component


def write_notices(packages: list[dict], output_path: Path) -> None:
    lines = [
        "# Backend third-party notices",
        "",
        "This file covers the exact non-project NuGet packages with runtime assets in this RID payload.",
        ".NET and ASP.NET Core runtime-pack notices are bundled separately under `licenses/dotnet/`.",
        "",
    ]
    for entry in packages:
        lines.extend([f"## {entry['name']} {entry['version']}", "", f"- License: {entry['licenseExpression']}"])
        if entry["authors"]:
            lines.append(f"- Authors: {entry['authors']}")
        if entry["copyright"]:
            lines.append(f"- Copyright: {entry['copyright']}")
        else:
            lines.append("- Copyright: Not stated in NuGet metadata; Authors are not inferred as copyright owners.")
        if entry["projectUrl"]:
            lines.append(f"- Project: {entry['projectUrl'].strip()}")
        if entry["licenseUrl"]:
            lines.append(f"- License metadata URL: {entry['licenseUrl'].strip()}")
        source_availability = entry.get("sourceAvailability")
        if source_availability:
            lines.extend(
                [
                    f"- Selected license basis: {source_availability['licenseBasis']}",
                    "- Covered source availability:",
                    f"  - Repository: {source_availability['sourceRepository']}",
                    f"  - Upstream tag: {source_availability['upstreamTag']}",
                    f"  - Immutable commit: {source_availability['sourceCommit']}",
                    f"  - Source archive: {source_availability['sourceArchive']}",
                ]
            )
        lines.append("- Bundled legal terms:")
        for term in entry["licenseFiles"]:
            lines.append(f"  - {term['role']}: `{term['path']}` (SHA-256 `{term['sha256']}`)")
        lines.append("")
    output_path.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def generate_corpus(
    rid: str,
    deps_path: Path,
    bundle_input_manifest: Path,
    metadata_path: Path,
    tool_manifest_path: Path,
    license_root: Path,
    packages_root: Path,
    output_directory: Path,
) -> int:
    tool_version = pinned_tool_version(tool_manifest_path)
    selected = shipped_packages(rid, deps_path, bundle_input_manifest)
    metadata, versions_by_name = metadata_index(metadata_path)
    license_output = output_directory / "licenses" / "nuget"
    if license_output.exists():
        shutil.rmtree(license_output)
    components: list[dict] = []
    for (folded_name, version), selection in selected.items():
        entry = metadata.get((folded_name, version))
        if entry is None:
            available = sorted(versions_by_name.get(folded_name, set()))
            if available:
                raise ValueError(
                    f"stale nuget-license metadata for {selection['name']}/{version}; found: {', '.join(available)}"
                )
            raise ValueError(f"shipped package {selection['name']}/{version} is missing from nuget-license metadata")
        components.append(
            build_component(rid, selection, version, entry, license_root, packages_root, output_directory)
        )
    components.sort(key=lambda entry: (entry["name"].casefold(), entry["version"], entry["name"]))
    output_directory.mkdir(parents=True, exist_ok=True)
    payload = {
        "$generated": "Generated by scripts/compliance/generate_backend_license_corpus.py; do not edit.",
        "metadataSource": {"tool": "nuget-license", "version": tool_version},
        "packages": components,
        "runtimeIdentifier": rid,
        "shipmentEvidence": {
            "method": "MSBuild FilesToBundle and loose ResolvedFileToPublish captured immediately before bundling",
            "sha256": hashlib.sha256(bundle_input_manifest.read_bytes()).hexdigest(),
        },
    }
    (output_directory / "backend-components.json").write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n"
    )
    write_notices(components, output_directory / "THIRD-PARTY-NOTICES.md")
    return len(components)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", choices=("linux-x64", "win-x64"), required=True)
    parser.add_argument("--deps-json", type=Path, required=True)
    parser.add_argument("--bundle-input-manifest", type=Path, required=True)
    parser.add_argument("--metadata-json", type=Path, required=True)
    parser.add_argument("--tool-manifest", type=Path, default=Path("dotnet-tools.json"))
    parser.add_argument("--license-root", type=Path, default=Path("third-party"))
    parser.add_argument("--nuget-packages-root", type=Path, default=Path.home() / ".nuget" / "packages")
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()
    count = generate_corpus(
        args.rid,
        args.deps_json,
        args.bundle_input_manifest,
        args.metadata_json,
        args.tool_manifest,
        args.license_root,
        args.nuget_packages_root,
        args.output_directory,
    )
    print(f"generated {args.rid} backend license corpus for {count} shipped NuGet packages")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
