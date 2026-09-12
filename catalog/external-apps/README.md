# External Apps catalog

The curated description of every containerised application XE can install. The engine never parses Compose: it
reads one JSON document, `dist/applications.json`, which this folder produces offline from upstream authoring
sources. That document ships as an embedded seed inside the host and is optionally refreshed from a pinned URL.

This README is for whoever adds the next application. It is not a description of the runtime; that lives in
`docs/wiki/`.

## Layout

```
catalog/external-apps/
  README.md                          this file
  applications/<id>/
    compose.yml                      byte-verbatim copy of the upstream compose, pinned to a commit
    variables.json                   every upstream environment name, in one of three buckets
    manifest.overrides.json          metadata, digests, provenance, ports, storage, files
    files/<service>/<name>           catalog-shipped read-only assets, inlined into the document
  tools/
    build_catalog.py                 the converter
    tests/                           its pytest suite
  dist/applications.json             generated; committed; never hand-edited
```

The generated document is also copied byte-for-byte to
`XE-Local-AI-Engine.Client.Application/Services/ExternalApps/Catalog/external-apps-catalog.seed.json`, the embedded
seed. Both are build output. A test asserts they are identical, so editing either by hand is a red build.

## Adding an application

1. **Copy the upstream compose verbatim** into `applications/<id>/compose.yml`, together with any config file the
   compose bind-mounts, and record the upstream repository, the exact commit and the fetch date in
   `manifest.overrides.json` under `provenance`. Pin a commit, never a branch: the C4, C5 and C7 gates below only
   mean something against a fixed input.
2. **Classify every environment name** the compose uses in `variables.json` (see the next section). The converter
   fails when a compose environment key belongs to no bucket, so an upstream bump that adds a setting turns the
   build red instead of silently ignoring it.
3. **Resolve every image digest** and record it with its tag, the command and the date (see below).
4. **Fill in `manifest.overrides.json`**: application metadata, the loopback ports XE publishes, the engine-owned
   storage entries that replace upstream's named volumes, the file entries, and one justification line per service
   that declares `capAdd`.
5. **Build**: `uv run python catalog/external-apps/tools/build_catalog.py`
6. **Gate**: `uv run pytest catalog/external-apps/tools/tests` and `scripts/python-validation.sh --scope changed`,
   then the backend build and `XE-Local-AI-Engine.Tests`. The C# validator is what decides whether the manifest is
   valid, so a converter that runs clean proves nothing on its own.

`build_catalog.py --check` rebuilds in memory and fails on any difference from the committed document **or the
embedded seed**, comparing raw text rather than reparsed objects, and printing a unified diff. It ignores
`generatedAtUtc`, which is the current time on every build. There is no single-application mode: both outputs are
whole-catalog files, so every write covers every application.

## The three buckets

Every environment name the upstream compose uses goes in exactly one bucket in `variables.json`.

- **exposed** — the user supplies it at install. Becomes an entry in the manifest's `variables[]` and is referenced
  from the service environment as `${NAME}`. An optional exposed variable with no user value and no default
  substitutes to the **empty string** and its key is still emitted, reproducing upstream's `${NAME:-}` behaviour;
  a dropped key and an empty key are different things to the application reading them.
- **fixed** — baked into `services[].environment` as a literal, never shown to the user. Use it for service DNS on
  the instance network, the security posture upstream's README says to leave alone, upstream defaults copied so
  behaviour matches, and the built-in substitutions `${XE_UID}`, `${XE_GID}`, `${XE_INSTANCE_ID}` and
  `${XE_UI_HOST_PORT_<service>}`.
- **dropped** — omitted from the manifest entirely, with a reason. Anything XE owns rather than the application:
  bind addresses and host ports, host storage paths, and settings that cannot work under a daemon-assigned
  loopback port.

Names that the compose documents but never interpolates are not manifest material and belong in no bucket.

## Images and digests

Every image is recorded as `<reference>@sha256:<64 hex digits>` plus a separate `imageTag`. The digest is what
actually runs, so a moved tag cannot change what installs.

**`latest` is never a recorded tag.** The validator rejects it. When upstream pins `:latest` or gives no tag at
all, pick a concrete released tag, record why in `manifest.overrides.json`, and pin its digest.

```bash
docker buildx imagetools inspect docker.io/<repo>:<tag> --format '{{json .Manifest}}'

# fallback when Docker is not available
TOKEN=$(curl -s "https://auth.docker.io/token?service=registry.docker.io&scope=repository:<repo>:pull" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
curl -sI -H "Authorization: Bearer $TOKEN" \
  -H "Accept: application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json" \
  https://registry-1.docker.io/v2/<repo>/manifests/<tag> | grep -i '^docker-content-digest:'
```

Record the resolved digest, the tag it came from, the command and the date under `provenance.digests`. A reviewer
must be able to re-run exactly what you ran.

## `manifestSha256`

The converter writes it and the engine re-checks it at catalog load; a manifest whose fingerprint does not match
its canonical form is rejected before it can be installed. It is what binds a user's permission acceptance to the
exact manifest they were shown, so **never hand-edit it and never hand-edit a manifest to match one**. Rebuild
instead. `build_catalog.manifest_fingerprint` is importable for tooling that has to re-fingerprint a mutated
manifest.

## `manifestVersion`

**Bump it in the same edit that changes a manifest.** `manifestSha256` is recomputed by the build, but the
version is what reaches an instance that is already installed: `ExternalAppService.UpdateAsync` treats a target
whose `manifestVersion` is not greater than the stored one as a no-op, so a manifest edited without a bump
changes fresh installs only and every existing instance keeps running on its stored snapshot — an operator who
presses Update is told it worked and nothing happens.

The build enforces it. `build_catalog.check_version_bumps` compares each rebuilt manifest against the document
committed at `HEAD` and fails when a fingerprint moved while its `manifestVersion` stood still. The baseline is
`HEAD`, not the file on disk, so rebuilding several times while authoring one change never asks for a second bump.

The Python and C# implementations are two independent pieces of code producing one contract, and
`ShippedCatalogSeedTests` is where a shipped manifest's committed `manifestSha256` is compared against a fresh
`ExternalAppManifestFingerprint.Compute`. **The shipped catalog is empty today, so that comparison currently has
nothing to run over** — the first application published to the catalog restores the cross-language proof, and until
then only `SampleCatalogManifestTests` pins the C# side of the canonical form, against a fixture the converter never
produced. The converter's own pytest suite can only prove the Python side is self-consistent.

## Permissions

- **`internet` must be `true`.** This version enforces no outbound restriction: every container sits on a bridge
  network with outbound access, and omitting `extraHosts` does not stop a container reaching a numeric LAN
  address. A `false` would be an unenforced promise, so the validator rejects it rather than shipping one.
- **`localNetwork` is a disclosure field, not a control.** It drives the wording of the install permissions panel
  and is required to be `true` whenever a service declares `extraHosts`, because reaching the host gateway *is*
  local-network access. It never restricts anything.
- `hostFiles` is one of `none`, `readOnly`, `readWrite`; `gpu` is one of `none`, `optional`, `required`.

## What the catalog refuses

- **Docker-socket access.** Variables such as `<APP>_ENABLE_HOST_DOCKER` and `DOCKER_GID` ask the application
  to drive the host's container runtime. The policy layer forbids it; drop such variables.
- **GPU devices.** No manifest requests GPU devices in this version. An application that needs acceleration uses
  an external model backend.
- `privileged`, `devices`, `network_mode`, `pid`, `ipc`, `userns_mode`, `security_opt`, `sysctls`, `ulimits`,
  `deploy`, `cgroup_parent`, top-level `secrets`, `configs`, `profiles`, `extends`, `include`, and a `networks`
  block with a driver. The converter fails on all of them rather than dropping them quietly.
- A `user` field. Containers start as the image's default user and the engine passes no `--user`; wire `PUID`
  and `PGID` to `${XE_UID}` and `${XE_GID}` instead. The boundary is the container, its dropped capabilities,
  seccomp, `no-new-privileges` and the loopback-only network, not the uid.

## Host layout

`files[].source` is relative to the application folder here — it is what the converter reads and what the
validator checks, never a host path. At install the engine materialises each entry at
`{instanceDir}/files/{service}/{source}` and each storage entry at `{instanceDir}/volumes/{service}/{name}`, both
namespaced by service, so two services that both declare a storage entry named `data` never share a host
directory.

## Publishing

Until the catalog repository exists, `ExternalApps:Catalog:RefreshUrl` ships empty and the engine serves the
embedded seed only. To publish:

1. Copy `dist/applications.json` to `catalog/applications.json` in `w0rldx/xe-external` and commit it on `main`.
2. Confirm it is served at
   `https://raw.githubusercontent.com/w0rldx/xe-external/main/catalog/applications.json`.
3. Set `ExternalApps:Catalog:RefreshUrl` to that URL. A configured URL must be `https`, or `http` to
   `127.0.0.1`, `::1` or `localhost`; anything else is logged once and ignored, and the catalog stays
   bundled-only.

The published document and the embedded seed are the same bytes. A refresh that fails or fails validation never
regresses a working catalog.
