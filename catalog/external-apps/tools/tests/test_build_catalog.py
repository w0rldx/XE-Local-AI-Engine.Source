"""Converter-level rules of build_catalog.py.

Only what the converter alone can get wrong is tested here: the C1..C7 gates, the compose quirks
that are silently wrong in both directions ($$ unescaping, scalar argv, colons inside ${...}), the
file inlining invariants and the cross-language fingerprint. Manifest validity itself belongs to
ExternalAppCatalogValidator on the C# side, which is the sole authority; mirroring ~50 rules here
would only produce two implementations that drift.
"""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path
from typing import Any

import pytest

import build_catalog

COMPOSE = """\
services:
  app:
    image: docker.io/library/app:1.0
    command: serve --quiet
    ports:
      - "${APP_BIND:-127.0.0.1}:9000:9000"
    volumes:
      - app-data:/var/lib/app
      - ./files/app/config.txt:/etc/app/config.txt:ro,z
    environment:
      - APP_TOKEN=${APP_TOKEN:-}
      - APP_MODE=${APP_MODE:-fast}
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O - http://localhost:9000/ >/dev/null"]
      interval: 5s
      timeout: 6s
      retries: 20
      start_period: 1m10s
    restart: unless-stopped

  side:
    image: docker.io/library/side:1.0
    entrypoint:
      - /bin/sh
      - -c
      - |
        secret="$${SIDE_SECRET:-}"
        echo "$$secret"
    extra_hosts:
      - "host.docker.internal:host-gateway"
    environment:
      - SIDE_LEVEL=${SIDE_LEVEL:-}
    depends_on:
      app:
        condition: service_healthy
    restart: unless-stopped

volumes:
  app-data:
"""

VARIABLES: dict[str, Any] = {
    "exposed": [
        {
            "name": "APP_TOKEN",
            "label": "Token",
            "description": "An API token.",
            "type": "secret",
            "required": False,
            "default": None,
            "allowedValues": None,
            "validation": None,
            "advanced": False,
        },
        {
            "name": "SIDE_LEVEL",
            "label": "Level",
            "description": None,
            "type": "string",
            "required": False,
            "default": None,
            "allowedValues": None,
            "validation": {"minLength": 1, "maxLength": 8, "pattern": None},
            "advanced": True,
        },
    ],
    "fixed": {"APP_MODE": "fast"},
    "dropped": [{"name": "APP_BIND", "reason": "XE owns port publishing."}],
}

OVERRIDES: dict[str, Any] = {
    "application": {
        "id": "fixture",
        "manifestVersion": 1,
        "displayName": "Fixture",
        "summary": "A fixture application.",
        "description": "A fixture application used by the converter tests.",
        "homepage": "https://example.com/fixture",
        "license": "MIT",
        "trust": "xeCatalog",
        "testedVersion": "1.0",
        "requires": ["containers", "networks", "bindStorage", "loopbackPortPublishing"],
        "permissions": {"internet": True, "localNetwork": True, "hostFiles": "none", "gpu": "none"},
        "resources": {"minimumMemoryMb": 512, "recommendedMemoryMb": 1024, "cpuHint": 1, "pidsLimit": 256},
    },
    "services": {
        "app": {
            "image": "docker.io/library/app@sha256:" + "a" * 64,
            "imageTag": "1.0",
            "capAdd": ["CHOWN"],
            "ports": [{"containerPort": 9000, "role": "ui", "preferredHostPort": 9000, "openPath": "/"}],
            "storage": [{"name": "data", "containerPath": "/var/lib/app"}],
            "files": [{"source": "files/app/config.txt", "containerPath": "/etc/app/config.txt"}],
        },
        "side": {
            "image": "docker.io/library/side@sha256:" + "b" * 64,
            "imageTag": "1.0",
            "ports": [],
            "storage": [],
            "files": [],
        },
    },
}

FILE_BODY = b"level = 1\n"


def write_fixture(root: Path, **changes: Any) -> Path:
    """Materialise the fixture application under ``root``; keyword arguments replace one input each."""
    application = root / "fixture"
    (application / "files" / "app").mkdir(parents=True)
    (application / "files" / "app" / "config.txt").write_bytes(changes.get("file_body", FILE_BODY))
    (application / "compose.yml").write_text(changes.get("compose", COMPOSE), encoding="utf-8")
    (application / "variables.json").write_text(json.dumps(changes.get("variables", VARIABLES)), encoding="utf-8")
    (application / "manifest.overrides.json").write_text(
        json.dumps(changes.get("overrides", OVERRIDES)), encoding="utf-8"
    )
    return application


def service(manifest: dict[str, Any], name: str) -> dict[str, Any]:
    return next(entry for entry in manifest["services"] if entry["name"] == name)


def replace_service(key: str, **fields: Any) -> dict[str, Any]:
    """Return a copy of OVERRIDES with ``fields`` merged into service ``key``."""
    overrides = json.loads(json.dumps(OVERRIDES))
    overrides["services"][key].update(fields)
    return overrides


def test_dollar_escapes_are_unescaped_in_the_entrypoint(tmp_path: Path) -> None:
    manifest = build_catalog.build_application(write_fixture(tmp_path))

    script = service(manifest, "side")["entrypoint"][2]
    assert 'secret="${SIDE_SECRET:-}"' in script
    assert 'echo "$secret"' in script
    assert "$$" not in script


def test_a_scalar_command_is_split_with_shlex(tmp_path: Path) -> None:
    manifest = build_catalog.build_application(write_fixture(tmp_path))

    assert service(manifest, "app")["command"] == ["serve", "--quiet"]
    assert service(manifest, "app")["entrypoint"] is None


def test_environment_is_assembled_from_the_classification(tmp_path: Path) -> None:
    manifest = build_catalog.build_application(write_fixture(tmp_path))

    assert service(manifest, "app")["environment"] == {"APP_TOKEN": "${APP_TOKEN}", "APP_MODE": "fast"}
    assert service(manifest, "side")["environment"] == {"SIDE_LEVEL": "${SIDE_LEVEL}"}


def test_compose_details_are_normalised(tmp_path: Path) -> None:
    manifest = build_catalog.build_application(write_fixture(tmp_path))

    assert service(manifest, "app")["healthcheck"] == {
        "test": ["CMD-SHELL", "wget -q -O - http://localhost:9000/ >/dev/null"],
        "intervalSeconds": 5,
        "timeoutSeconds": 6,
        "retries": 20,
        "startPeriodSeconds": 70,
    }
    assert service(manifest, "side")["dependsOn"] == [{"service": "app", "condition": "healthy"}]
    assert service(manifest, "side")["extraHosts"] == ["host-gateway"]
    assert service(manifest, "app")["capAdd"] == ["CHOWN"]


def test_a_file_body_is_inlined_with_its_sha256(tmp_path: Path) -> None:
    manifest = build_catalog.build_application(write_fixture(tmp_path))

    entry = service(manifest, "app")["files"][0]
    assert base64.b64decode(entry["contentBase64"]) == FILE_BODY
    assert entry["sha256"] == hashlib.sha256(FILE_BODY).hexdigest()


def test_a_file_body_over_the_limit_is_refused(tmp_path: Path) -> None:
    application = write_fixture(tmp_path, file_body=b"x" * (build_catalog.MAX_FILE_BYTES + 1))

    with pytest.raises(build_catalog.CatalogBuildError, match="over the 65536-byte limit"):
        build_catalog.build_application(application)


def test_c1_refuses_a_built_service_without_an_image_override(tmp_path: Path) -> None:
    compose = COMPOSE.replace("    image: docker.io/library/app:1.0\n", "    build: .\n")
    overrides = replace_service("app", image=None)

    with pytest.raises(build_catalog.CatalogBuildError, match="C1"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose, overrides=overrides))


def test_c2_refuses_an_image_without_a_digest(tmp_path: Path) -> None:
    overrides = replace_service("app", image="docker.io/library/app:1.0")

    with pytest.raises(build_catalog.CatalogBuildError, match="C2.*not pinned"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c2_refuses_the_latest_tag(tmp_path: Path) -> None:
    overrides = replace_service("app", imageTag="latest")

    with pytest.raises(build_catalog.CatalogBuildError, match="C2.*latest"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c3_refuses_a_rejected_service_key(tmp_path: Path) -> None:
    compose = COMPOSE.replace("    command: serve --quiet\n", "    privileged: true\n")

    with pytest.raises(build_catalog.CatalogBuildError, match="C3.*privileged"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose))


def test_c3_refuses_a_top_level_secrets_block(tmp_path: Path) -> None:
    compose = COMPOSE + "\nsecrets:\n  token:\n    file: ./token\n"

    with pytest.raises(build_catalog.CatalogBuildError, match="C3.*secrets"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose))


def test_c3_refuses_an_unknown_extra_host(tmp_path: Path) -> None:
    compose = COMPOSE.replace('"host.docker.internal:host-gateway"', '"registry.local:10.0.0.5"')

    with pytest.raises(build_catalog.CatalogBuildError, match="C3.*extra_hosts"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose))


def test_c4_refuses_an_unclassified_environment_key(tmp_path: Path) -> None:
    compose = COMPOSE.replace(
        "      - APP_MODE=${APP_MODE:-fast}\n", "      - APP_MODE=${APP_MODE:-fast}\n      - APP_NEW=1\n"
    )

    with pytest.raises(build_catalog.CatalogBuildError, match="C4.*APP_NEW"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose))


def test_c4_refuses_a_name_classified_twice(tmp_path: Path) -> None:
    variables = json.loads(json.dumps(VARIABLES))
    variables["fixed"]["SIDE_LEVEL"] = "3"

    with pytest.raises(build_catalog.CatalogBuildError, match="C4.*more than once"):
        build_catalog.build_application(write_fixture(tmp_path, variables=variables))


def test_c5_refuses_an_unclaimed_mount(tmp_path: Path) -> None:
    overrides = replace_service("app", storage=[])

    with pytest.raises(build_catalog.CatalogBuildError, match="C5.*/var/lib/app"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c5_refuses_a_claimed_mount_the_compose_does_not_mount(tmp_path: Path) -> None:
    overrides = replace_service(
        "app",
        storage=[{"name": "data", "containerPath": "/var/lib/app"}, {"name": "extra", "containerPath": "/srv/extra"}],
    )

    with pytest.raises(build_catalog.CatalogBuildError, match="C5.*/srv/extra"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


# An image VOLUME the compose does not mount is invisible to this offline converter but real on the created
# container, where the engine refuses it as an undeclared mount. The S5 live round hit exactly that: the
# searxng image declares /var/cache/searxng and upstream's compose leaves it anonymous, so the install failed
# its post-start policy check. imageVolumes[] is how an author states it; it exempts the entry from the
# "the compose does not mount this" half of C5 and nothing else.
def test_c5_accepts_a_storage_entry_declared_as_an_image_volume(tmp_path: Path) -> None:
    overrides = replace_service(
        "app",
        imageVolumes=["/srv/cache"],
        storage=[{"name": "data", "containerPath": "/var/lib/app"}, {"name": "cache", "containerPath": "/srv/cache"}],
    )

    application = build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))

    service = next(entry for entry in application["services"] if entry["name"] == "app")
    assert [entry["containerPath"] for entry in service["storage"]] == ["/var/lib/app", "/srv/cache"]


def test_c5_refuses_an_image_volume_with_no_storage_entry(tmp_path: Path) -> None:
    overrides = replace_service(
        "app",
        imageVolumes=["/srv/cache"],
        storage=[{"name": "data", "containerPath": "/var/lib/app"}],
    )

    with pytest.raises(build_catalog.CatalogBuildError, match="C5.*imageVolumes.*/srv/cache"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c6_refuses_a_foreign_restart_policy(tmp_path: Path) -> None:
    compose = COMPOSE.replace("    restart: unless-stopped\n\n  side:", "    restart: always\n\n  side:")

    with pytest.raises(build_catalog.CatalogBuildError, match="C6.*always"):
        build_catalog.build_application(write_fixture(tmp_path, compose=compose))


def test_c7_refuses_an_unclaimed_published_port(tmp_path: Path) -> None:
    overrides = replace_service("app", ports=[])

    with pytest.raises(build_catalog.CatalogBuildError, match=r"C7.*\[9000\]"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c7_accepts_a_published_port_that_is_deliberately_ignored(tmp_path: Path) -> None:
    overrides = replace_service("app", ports=[], ignoredPorts=[{"containerPort": 9000, "reason": "internal only"}])
    overrides["services"]["side"]["ports"] = [
        {"containerPort": 1, "role": "ui", "preferredHostPort": None, "openPath": "/"}
    ]

    with pytest.raises(build_catalog.CatalogBuildError, match=r"C7.*\[1\]"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_c7_refuses_an_ignored_port_without_a_reason(tmp_path: Path) -> None:
    overrides = replace_service("app", ports=[], ignoredPorts=[{"containerPort": 9000}])

    with pytest.raises(build_catalog.CatalogBuildError, match="C7.*no reason"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_the_fingerprint_hashes_compact_json_without_the_hash_key() -> None:
    """Fixed input, expected digest taken over a literal canonical string.

    The previous version re-typed manifest_fingerprint's own json.dumps line and compared the result to
    manifest_fingerprint's output, which is true for any serialisation the two happen to share -- it would have
    passed just as well with indent=2 on both sides. Here the canonical form is written out by hand, so the
    assertion fails if the key sorting, the separators, the ascii escaping or the hash-key exclusion changes.
    """
    manifest = {
        "manifestVersion": 1,
        build_catalog.HASH_KEY: "0" * 64,
        "id": "fixture",
        "displayName": "Fixture \u00e4",
        "services": [],
    }
    canonical = '{"displayName":"Fixture \u00e4","id":"fixture","manifestVersion":1,"services":[]}'

    assert build_catalog.manifest_fingerprint(manifest) == hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def test_the_fingerprint_is_stable_across_two_builds(tmp_path: Path) -> None:
    first = build_catalog.build_application(write_fixture(tmp_path / "a"))
    second = build_catalog.build_application(write_fixture(tmp_path / "b"))

    assert first[build_catalog.HASH_KEY] == second[build_catalog.HASH_KEY]


def test_the_fingerprint_changes_when_a_file_body_changes(tmp_path: Path) -> None:
    baseline = build_catalog.build_application(write_fixture(tmp_path / "a"))
    mutated = build_catalog.build_application(write_fixture(tmp_path / "b", file_body=b"level = 2\n"))

    assert mutated[build_catalog.HASH_KEY] != baseline[build_catalog.HASH_KEY]


def test_the_fingerprint_changes_when_a_fixed_environment_value_changes(tmp_path: Path) -> None:
    variables = json.loads(json.dumps(VARIABLES))
    variables["fixed"]["APP_MODE"] = "slow"

    baseline = build_catalog.build_application(write_fixture(tmp_path / "a"))
    mutated = build_catalog.build_application(write_fixture(tmp_path / "b", variables=variables))

    assert mutated[build_catalog.HASH_KEY] != baseline[build_catalog.HASH_KEY]


def test_the_fingerprint_changes_when_a_published_port_changes(tmp_path: Path) -> None:
    overrides = replace_service(
        "app", ports=[{"containerPort": 9000, "role": "ui", "preferredHostPort": 9100, "openPath": "/"}]
    )

    baseline = build_catalog.build_application(write_fixture(tmp_path / "a"))
    mutated = build_catalog.build_application(write_fixture(tmp_path / "b", overrides=overrides))

    assert mutated[build_catalog.HASH_KEY] != baseline[build_catalog.HASH_KEY]


def test_the_converter_has_no_single_application_write_mode() -> None:
    """--application filtered the applications and then wrote the filtered document over BOTH outputs, deleting
    the rest. There is no single-application mode; every write covers the whole catalog."""
    with pytest.raises(SystemExit):
        build_catalog.main(["--application", "odysseus"])


def test_a_non_integral_resource_value_is_refused(tmp_path: Path) -> None:
    overrides = json.loads(json.dumps(OVERRIDES))
    overrides["application"]["resources"]["pidsLimit"] = 256.5

    with pytest.raises(build_catalog.CatalogBuildError, match="resources.pidsLimit must be an integer"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_an_integral_float_is_coerced_to_an_int(tmp_path: Path) -> None:
    overrides = replace_service(
        "app", ports=[{"containerPort": 9000.0, "role": "ui", "preferredHostPort": 9000.0, "openPath": "/"}]
    )

    port = service(build_catalog.build_application(write_fixture(tmp_path, overrides=overrides)), "app")["ports"][0]

    assert port["containerPort"] == 9000
    assert isinstance(port["containerPort"], int)
    assert isinstance(port["preferredHostPort"], int)


def test_a_boolean_is_never_accepted_where_an_integer_belongs(tmp_path: Path) -> None:
    overrides = json.loads(json.dumps(OVERRIDES))
    overrides["application"]["manifestVersion"] = True

    with pytest.raises(build_catalog.CatalogBuildError, match="manifestVersion must be an integer, got the boolean"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_an_absolute_file_source_is_refused(tmp_path: Path) -> None:
    overrides = replace_service("app", files=[{"source": "/etc/hostname", "containerPath": "/etc/app/config.txt"}])

    with pytest.raises(build_catalog.CatalogBuildError, match="resolves outside"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_a_file_source_that_escapes_the_application_folder_is_refused(tmp_path: Path) -> None:
    (tmp_path / "outside.txt").write_bytes(b"secret\n")
    overrides = replace_service("app", files=[{"source": "../outside.txt", "containerPath": "/etc/app/config.txt"}])

    with pytest.raises(build_catalog.CatalogBuildError, match="resolves outside"):
        build_catalog.build_application(write_fixture(tmp_path, overrides=overrides))


def test_a_symlink_that_escapes_the_application_folder_is_refused(tmp_path: Path) -> None:
    application = write_fixture(tmp_path)
    (tmp_path / "outside.txt").write_bytes(b"secret\n")
    (application / "files" / "app" / "config.txt").unlink()
    (application / "files" / "app" / "config.txt").symlink_to(tmp_path / "outside.txt")

    with pytest.raises(build_catalog.CatalogBuildError, match="resolves outside"):
        build_catalog.build_application(application)


def test_an_oversized_file_is_refused_before_it_is_read(tmp_path: Path) -> None:
    application = write_fixture(tmp_path, file_body=b"x" * (build_catalog.MAX_FILE_BYTES + 1))
    read_bytes = Path.read_bytes

    def refuse(self: Path) -> bytes:
        if self.name == "config.txt":
            raise AssertionError("the oversized body was read before the size gate ran")
        return read_bytes(self)

    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(Path, "read_bytes", refuse)
        with pytest.raises(build_catalog.CatalogBuildError, match="over the 65536-byte limit"):
            build_catalog.build_application(application)


def test_check_compares_raw_text_and_opens_the_embedded_seed(tmp_path: Path) -> None:
    """A reformatted seed parses to the same object as dist/applications.json. The old --check reparsed both and
    never opened the seed at all, so it reported green on a file the C# byte-identity test would fail on."""
    seed = tmp_path / "external-apps-catalog.seed.json"
    seed.write_text(build_catalog.SEED_PATH.read_text(encoding="utf-8").replace("\n", "\n ", 1), encoding="utf-8")

    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(build_catalog, "SEED_PATH", seed)
        assert build_catalog.main(["--check"]) == 1


def test_two_builds_of_the_repository_tree_produce_identical_bytes() -> None:
    first = build_catalog.build_document()
    second = build_catalog.build_document()
    second["generatedAtUtc"] = first["generatedAtUtc"]

    assert build_catalog.serialize(first) == build_catalog.serialize(second)


def test_check_reports_the_committed_document_as_up_to_date() -> None:
    assert build_catalog.main(["--check"]) == 0
