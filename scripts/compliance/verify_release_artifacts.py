#!/usr/bin/env python3
"""Verify final portable artifacts rather than trusting dependency declarations."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
FORBIDDEN_MARKERS = (
    b"kokoro",
    b"phonemizer",
    b"espeakng",
    b"microsoft.agents.ai.devui",
    b"microsoft.agents.ai.hosting.dll",
    b"microsoft.agents.ai.hosting.openai.dll",
)
DOTNET_RUNTIME_DOCUMENT_NAMES = (
    "DOTNET-RUNTIME-LICENSE.txt",
    "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
    "ASPNETCORE-RUNTIME-LICENSE.txt",
    "ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt",
)
COMMON_REQUIRED = (
    "_manifest/spdx_2.2/manifest.spdx.json",
    "wwwroot/component-manifest.json",
    "wwwroot/licenses/frontend/frontend-components.json",
    "wwwroot/licenses/frontend/third-party-notices.md",
    "backend-components.json",
    "third-party-notices.md",
)
RID_REQUIRED = {
    "win-x64": (
        "xe-local-ai-engine.windowslauncher.exe",
        "xe-local-ai-engine.windowslauncher.dll",
        "xe-local-ai-engine.windowslauncher.deps.json",
        "xe-local-ai-engine.windowslauncher.runtimeconfig.json",
        "xe-local-ai-engine.client.dll",
        "xe-local-ai-engine.client.deps.json",
        "xe-local-ai-engine.client.runtimeconfig.json",
        "licenses/dotnet/dotnet-apphost-license.txt",
        "licenses/dotnet/dotnet-apphost-third-party-notices.txt",
        "wwwroot/licenses/dotnet/dotnet-apphost-license.txt",
        "wwwroot/licenses/dotnet/dotnet-apphost-third-party-notices.txt",
    ),
    "linux-x64": (
        *(f"licenses/dotnet/{name.lower()}" for name in DOTNET_RUNTIME_DOCUMENT_NAMES),
        *(f"wwwroot/licenses/dotnet/{name.lower()}" for name in DOTNET_RUNTIME_DOCUMENT_NAMES),
    ),
}
WINDOWS_FORBIDDEN_RUNTIME_SUFFIXES = (
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "clrjit.dll",
    "system.private.corelib.dll",
    "dotnet-library-license.html",
)


def select_current_full_package(release_dir: Path, version: str) -> Path:
    packages = sorted(release_dir.glob("*-full.nupkg"))
    current = [package for package in packages if f"-{version}-" in package.name]
    if len(current) != 1:
        raise ValueError(f"expected one full package for {version} in {release_dir}, found {len(current)}")
    stale = [package.name for package in packages if package != current[0]]
    if stale:
        raise ValueError(f"release output still contains previous full package(s): {stale}")
    return current[0]


def required_suffixes(runtime_identifier: str) -> tuple[str, ...]:
    try:
        return COMMON_REQUIRED + RID_REQUIRED[runtime_identifier]
    except KeyError as error:
        raise ValueError(f"unsupported runtime identifier: {runtime_identifier}") from error


def assert_required_paths(paths: list[str], runtime_identifier: str, label: str) -> None:
    normalized = [path.replace("\\", "/").lower() for path in paths]
    for required in required_suffixes(runtime_identifier):
        if not any(path.endswith(required) for path in normalized):
            raise ValueError(f"{label} is missing required {required.upper()}")


def assert_framework_distribution(paths: list[str], runtime_identifier: str, label: str) -> None:
    if runtime_identifier != "win-x64":
        return
    normalized = [path.replace("\\", "/").lower() for path in paths]
    forbidden = next(
        (path for path in normalized if any(path.endswith(suffix) for suffix in WINDOWS_FORBIDDEN_RUNTIME_SUFFIXES)),
        None,
    )
    if forbidden is not None:
        raise ValueError(
            f"{label} framework-dependent Windows payload contains forbidden runtime material: {forbidden}"
        )


def assert_no_removed_tts(path: str, content: bytes, label: str) -> None:
    searchable = path.lower().encode("utf-8", errors="ignore") + b"\n" + content.lower()
    if any(marker in searchable for marker in FORBIDDEN_MARKERS):
        raise ValueError(f"{label} contains forbidden release payload marker in {path}")


def assert_stream_has_no_forbidden_markers(path: str, stream, label: str) -> None:
    assert_no_removed_tts(path, b"", label)
    overlap = max(len(marker) for marker in FORBIDDEN_MARKERS) - 1
    trailing = b""
    while chunk := stream.read(1024 * 1024):
        searchable = trailing + chunk.lower()
        if any(marker in searchable for marker in FORBIDDEN_MARKERS):
            raise ValueError(f"{label} contains forbidden release payload marker in {path}")
        trailing = searchable[-overlap:]


def component_purls(content: bytes, label: str) -> set[str]:
    try:
        payload = json.loads(content)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} has an invalid frontend component manifest: {error}") from error
    components = payload.get("components") if isinstance(payload, dict) else None
    if not isinstance(components, list) or not components:
        raise ValueError(f"{label} frontend component manifest has no detected components")
    purls = {
        component.get("purl")
        for component in components
        if isinstance(component, dict) and isinstance(component.get("purl"), str)
    }
    if len(purls) != len(components):
        raise ValueError(f"{label} frontend component manifest has missing or duplicate purls")
    return purls


def assert_component_corpus_matches(detected: set[str], licensed: set[str], label: str) -> None:
    if detected != licensed:
        raise ValueError(
            f"{label} frontend license corpus does not match detected components: "
            f"unlicensed={sorted(detected - licensed)} stale={sorted(licensed - detected)}"
        )


def frontend_legal_terms(content: bytes, label: str) -> dict[str, str]:
    try:
        payload = json.loads(content)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} has an invalid frontend component manifest: {error}") from error
    components = payload.get("components") if isinstance(payload, dict) else None
    if not isinstance(components, list) or not components:
        raise ValueError(f"{label} frontend component manifest has no detected components")
    terms: dict[str, str] = {}
    for component in components:
        license_files = component.get("licenseFiles") if isinstance(component, dict) else None
        if not isinstance(license_files, list) or not license_files:
            raise ValueError(f"{label} frontend component has no bundled legal terms")
        for entry in license_files:
            path = entry.get("path") if isinstance(entry, dict) else None
            digest = entry.get("sha256") if isinstance(entry, dict) else None
            if (
                not isinstance(path, str)
                or not path
                or Path(path).is_absolute()
                or ".." in Path(path).parts
                or path in terms
                or not isinstance(digest, str)
                or len(digest) != 64
                or any(character not in "0123456789abcdefABCDEF" for character in digest)
            ):
                raise ValueError(f"{label} frontend component manifest has an invalid legal term")
            terms[path] = digest.casefold()
    return terms


def assert_frontend_terms(terms: dict[str, str], contents: dict[str, bytes], label: str) -> None:
    for path, expected in terms.items():
        content = contents.get(path)
        if content is None:
            raise ValueError(f"{label} is missing frontend legal term {path}")
        if hashlib.sha256(content).hexdigest() != expected:
            raise ValueError(f"{label} frontend legal term checksum mismatch: {path}")


def backend_legal_terms(content: bytes, runtime_identifier: str, label: str) -> dict[str, str]:
    try:
        payload = json.loads(content)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} has an invalid backend component manifest: {error}") from error
    if not isinstance(payload, dict) or payload.get("runtimeIdentifier") != runtime_identifier:
        raise ValueError(f"{label} backend component manifest has the wrong runtime identifier")
    packages = payload.get("packages")
    if not isinstance(packages, list) or not packages:
        raise ValueError(f"{label} backend component manifest has no packages")
    terms: dict[str, str] = {}
    for package in packages:
        if not isinstance(package, dict):
            raise ValueError(f"{label} backend component manifest has an invalid package")
        license_files = package.get("licenseFiles")
        if not isinstance(license_files, list) or not license_files:
            raise ValueError(f"{label} backend package has no bundled legal terms")
        for entry in license_files:
            path = entry.get("path") if isinstance(entry, dict) else None
            digest = entry.get("sha256") if isinstance(entry, dict) else None
            if (
                not isinstance(path, str)
                or not path.startswith("licenses/nuget/")
                or Path(path).is_absolute()
                or ".." in Path(path).parts
                or path in terms
                or not isinstance(digest, str)
                or len(digest) != 64
            ):
                raise ValueError(f"{label} backend component manifest has an invalid legal term")
            terms[path] = digest.casefold()
    return terms


def assert_backend_terms(terms: dict[str, str], contents: dict[str, bytes], label: str) -> None:
    for path, expected in terms.items():
        content = contents.get(path)
        if content is None:
            raise ValueError(f"{label} is missing backend legal term {path}")
        if hashlib.sha256(content).hexdigest() != expected:
            raise ValueError(f"{label} backend legal term checksum mismatch: {path}")


def assert_dotnet_documents_are_served(contents: dict[str, bytes], runtime_identifier: str, label: str) -> None:
    names = (
        ("DOTNET-APPHOST-LICENSE.txt", "DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt")
        if runtime_identifier == "win-x64"
        else DOTNET_RUNTIME_DOCUMENT_NAMES
    )
    for name in names:
        bundled = contents.get(f"licenses/dotnet/{name}")
        served = contents.get(f"wwwroot/licenses/dotnet/{name}")
        if bundled is None or served is None:
            raise ValueError(f"{label} is missing bundled or served .NET legal document {name}")
        if bundled != served:
            raise ValueError(f"{label} served .NET legal document differs from bundled corpus: {name}")


def assert_project_documents(contents: dict[str, bytes], label: str) -> None:
    for name in ("LICENSE", "NOTICE"):
        content = contents.get(name)
        if content is None:
            raise ValueError(f"{label} is missing required {name}")
        if content != (REPOSITORY_ROOT / name).read_bytes():
            raise ValueError(f"{label} project {name} differs from the checked-out source")


def verify_zip_archive(archive: Path, runtime_identifier: str) -> None:
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        assert_required_paths(names, runtime_identifier, str(archive))
        assert_framework_distribution(names, runtime_identifier, str(archive))
        component_entries = [
            name for name in names if name.replace("\\", "/").lower().endswith("wwwroot/component-manifest.json")
        ]
        if len(component_entries) != 1:
            raise ValueError(f"{archive} must contain exactly one frontend component manifest")
        detected = component_purls(package.read(component_entries[0]), str(archive))
        corpus_entries = [
            name
            for name in names
            if name.replace("\\", "/").lower().endswith("wwwroot/licenses/frontend/frontend-components.json")
        ]
        if len(corpus_entries) != 1:
            raise ValueError(f"{archive} must contain exactly one frontend corpus inventory")
        corpus_entry = corpus_entries[0]
        corpus_content = package.read(corpus_entry)
        licensed = component_purls(corpus_content, str(archive))
        assert_component_corpus_matches(detected, licensed, str(archive))
        frontend_terms = frontend_legal_terms(corpus_content, str(archive))
        corpus_prefix = corpus_entry[: -len("FRONTEND-COMPONENTS.json")]
        assert_frontend_terms(
            frontend_terms,
            {
                path: package.read(f"{corpus_prefix}{path}")
                for path in frontend_terms
                if f"{corpus_prefix}{path}" in names
            },
            str(archive),
        )
        backend_entries = [
            name for name in names if name.replace("\\", "/").lower().endswith("backend-components.json")
        ]
        if len(backend_entries) != 1:
            raise ValueError(f"{archive} must contain exactly one backend component manifest")
        backend_entry = backend_entries[0]
        prefix = backend_entry[: -len("backend-components.json")]
        assert_project_documents(
            {
                document: package.read(f"{prefix}{document}")
                for document in ("LICENSE", "NOTICE")
                if f"{prefix}{document}" in names
            },
            str(archive),
        )
        assert_dotnet_documents_are_served(
            {
                relative: package.read(f"{prefix}{relative}")
                for name in (
                    ("DOTNET-APPHOST-LICENSE.txt", "DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt")
                    if runtime_identifier == "win-x64"
                    else DOTNET_RUNTIME_DOCUMENT_NAMES
                )
                for relative in (f"licenses/dotnet/{name}", f"wwwroot/licenses/dotnet/{name}")
                if f"{prefix}{relative}" in names
            },
            runtime_identifier,
            str(archive),
        )
        terms = backend_legal_terms(package.read(backend_entry), runtime_identifier, str(archive))
        assert_backend_terms(
            terms,
            {path: package.read(f"{prefix}{path}") for path in terms if f"{prefix}{path}" in names},
            str(archive),
        )
        for entry in package.infolist():
            if entry.is_dir():
                continue
            with package.open(entry) as stream:
                assert_stream_has_no_forbidden_markers(entry.filename, stream, str(archive))


def verify_tree(root: Path, runtime_identifier: str, label: str) -> None:
    files = [path for path in root.rglob("*") if path.is_file()]
    assert_required_paths([str(path.relative_to(root)) for path in files], runtime_identifier, label)
    assert_framework_distribution([str(path.relative_to(root)) for path in files], runtime_identifier, label)
    component_files = [
        path
        for path in files
        if str(path.relative_to(root)).replace("\\", "/").lower().endswith("wwwroot/component-manifest.json")
    ]
    if len(component_files) != 1:
        raise ValueError(f"{label} must contain exactly one frontend component manifest")
    detected = component_purls(component_files[0].read_bytes(), label)
    corpus_files = [
        path
        for path in files
        if str(path.relative_to(root))
        .replace("\\", "/")
        .lower()
        .endswith("wwwroot/licenses/frontend/frontend-components.json")
    ]
    if len(corpus_files) != 1:
        raise ValueError(f"{label} must contain exactly one frontend corpus inventory")
    corpus_file = corpus_files[0]
    corpus_content = corpus_file.read_bytes()
    licensed = component_purls(corpus_content, label)
    assert_component_corpus_matches(detected, licensed, label)
    frontend_terms = frontend_legal_terms(corpus_content, label)
    assert_frontend_terms(
        frontend_terms,
        {
            path: (corpus_file.parent / path).read_bytes()
            for path in frontend_terms
            if (corpus_file.parent / path).is_file()
        },
        label,
    )
    backend_files = [
        path
        for path in files
        if str(path.relative_to(root)).replace("\\", "/").lower().endswith("backend-components.json")
    ]
    if len(backend_files) != 1:
        raise ValueError(f"{label} must contain exactly one backend component manifest")
    terms = backend_legal_terms(backend_files[0].read_bytes(), runtime_identifier, label)
    application_root = backend_files[0].parent
    assert_project_documents(
        {
            document: (application_root / document).read_bytes()
            for document in ("LICENSE", "NOTICE")
            if (application_root / document).is_file()
        },
        label,
    )
    assert_dotnet_documents_are_served(
        {
            relative: (application_root / relative).read_bytes()
            for name in (
                ("DOTNET-APPHOST-LICENSE.txt", "DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt")
                if runtime_identifier == "win-x64"
                else DOTNET_RUNTIME_DOCUMENT_NAMES
            )
            for relative in (f"licenses/dotnet/{name}", f"wwwroot/licenses/dotnet/{name}")
            if (application_root / relative).is_file()
        },
        runtime_identifier,
        label,
    )
    assert_backend_terms(
        terms,
        {path: (application_root / path).read_bytes() for path in terms if (application_root / path).is_file()},
        label,
    )
    for path in files:
        with path.open("rb") as stream:
            assert_stream_has_no_forbidden_markers(str(path.relative_to(root)), stream, label)


def verify_appimage(appimage: Path, runtime_identifier: str) -> None:
    with tempfile.TemporaryDirectory(prefix="xe-appimage-") as temporary_directory:
        appimage.chmod(appimage.stat().st_mode | 0o100)
        result = subprocess.run(
            [str(appimage.resolve()), "--appimage-extract"],
            cwd=temporary_directory,
            check=False,
            capture_output=True,
            text=True,
            env={**os.environ, "APPIMAGE_SILENT_INSTALL": "1"},
        )
        if result.returncode != 0:
            raise ValueError(f"could not extract {appimage}: {result.stderr.strip() or result.stdout.strip()}")
        verify_tree(Path(temporary_directory) / "squashfs-root", runtime_identifier, str(appimage))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_linux_full_package(full_package: Path, standalone_appimage: Path) -> None:
    with zipfile.ZipFile(full_package) as package:
        appimages = [
            entry for entry in package.infolist() if not entry.is_dir() and entry.filename.lower().endswith(".appimage")
        ]
        if len(appimages) != 1:
            raise ValueError(f"{full_package} must contain exactly one embedded AppImage, found {len(appimages)}")
        with tempfile.TemporaryDirectory(prefix="xe-full-package-") as temporary_directory:
            embedded = Path(temporary_directory) / "embedded.AppImage"
            with package.open(appimages[0]) as source, embedded.open("wb") as destination:
                shutil.copyfileobj(source, destination)
            if sha256_file(embedded) != sha256_file(standalone_appimage):
                raise ValueError(f"{full_package} embedded AppImage does not match the standalone AppImage")
            verify_appimage(embedded, "linux-x64")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", choices=sorted(RID_REQUIRED), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--release-dir", type=Path, required=True)
    args = parser.parse_args()

    verify_tree(args.publish_dir, args.rid, str(args.publish_dir))
    full_package = select_current_full_package(args.release_dir, args.version)

    if args.rid == "win-x64":
        verify_zip_archive(full_package, args.rid)
        setup_files = list(args.release_dir.glob("*Setup.exe"))
        if setup_files:
            raise ValueError(f"portable-only Windows release contains installer: {setup_files[0].name}")
        portable_archives = sorted(args.release_dir.glob("*Portable.zip"))
        if len(portable_archives) != 1:
            raise ValueError(f"expected one Windows Portable.zip, found {len(portable_archives)}")
        verify_zip_archive(portable_archives[0], args.rid)
    else:
        appimages = sorted(args.release_dir.glob("*.AppImage"))
        if len(appimages) != 1:
            raise ValueError(f"expected one Linux AppImage, found {len(appimages)}")
        verify_linux_full_package(full_package, appimages[0])
        verify_appimage(appimages[0], args.rid)

    print(f"verified {args.rid} publish payload and final Velopack portable artifacts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
