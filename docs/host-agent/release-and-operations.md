# Local Node Runtime Release and Operations Guide

This guide explains how to continue working with the implemented local node runtime after the `local-node-runtime-mvp-plan` work. It is written for developers preparing a release, operators validating a node, and future maintainers who need to understand which checks must pass before the runtime is shipped.

For architecture and install-mode details, read these companion docs first:

- [Architecture](./architecture.md)
- [Aspire development modes](./aspire-dev.md)
- [Windows installation](./install-windows.md)
- [Linux installation](./install-linux.md)
- [Tray launcher](./tray.md)
- [Bring your own runtime](./byo.md)
- [Troubleshooting](./troubleshooting.md)

## Current runtime shape

The local node runtime is intentionally node-side only:

- The Node Web Server (`XE-Local-AI-Engine.Client`) remains the only component that talks to the central platform over the existing `WorkerHub` SignalR channel.
- HostAgent.Windows, HostAgent.Linux, and the Tray are local substrate components only. They do not connect to the platform.
- HostAgent.Linux owns rootless Docker, manifest reconciliation, container lifecycle, capabilities, and the local gRPC server.
- The Tray is the user entry point and status surface. It is not a management UI; the Node Web Server serves the React Web UI for management workflows.
- The bootstrap model is pulled before `WorkerHub` connects. Larger/default models are pulled on demand and may have first-call latency.

## Continue-working checklist

Before starting new runtime work, verify the scope and mark each item explicitly:

| Marker | Meaning |
| --- | --- |
| `(planned)` | Work is described, scoped, and has acceptance criteria. |
| `(started)` | Implementation or documentation has begun. |
| `(completed)` | Required validation passed and evidence is available. |
| `(blocked: reason)` | Work cannot continue until the stated missing evidence or dependency is resolved. |

Use this lifecycle for follow-up tasks, release tasks, and documentation updates. Do not mark an item `(completed)` until the matching tests or validation steps have passed.

When changing runtime behavior, verify these boundaries first:

1. **Platform boundary:** no platform-side changes unless a separate platform plan is approved.
2. **Provider behavior:** the current MVP is local/Ollama-first. Provider changes or cloud-provider switching must document whether a restart is required and must not leak provider secrets outside the worker-local scope.
3. **Secret handling:** worker credentials, admin tokens, HMAC secrets, cloud credentials, and external endpoint tokens stay local. Never place raw secrets in release artifacts, logs, transcripts, tickets, or docs.
4. **Admin endpoint security:** local admin HTTP must bind to loopback only, require bearer auth, reject unsafe `Host`/`Origin` patterns, and never log token values.
5. **Installer/runtime identity:** Windows managed mode uses HostAgent.Windows + WSL `xe-engine`; Linux native mode uses the desktop user. Do not add services, boot autostart, Run keys, scheduled tasks, XDG autostart, or native-Linux linger without a new approved plan.

## Release readiness gates

A release candidate is ready only when every applicable gate below has evidence.

### 1. Source and configuration verification

- Confirm the release branch contains only intended node-side changes.
- Confirm `Apps/XE-Local-AI-Engine/XE-Local-AI-Engine.slnx` includes the runtime projects that need to ship.
- Confirm package versions remain pinned in `Apps/XE-Local-AI-Engine/Directory.Packages.props`.
- Confirm manifests use digest-pinned images and do not use `:latest`.
- Confirm schema output under `Plans/artifacts/schemas/` matches the source schemas after build.
- Confirm sample manifests under `Plans/artifacts/sample-manifests/` validate against the generated schema.

### 2. Build and unit/integration validation

Run the XE subtree validation from `Apps/XE-Local-AI-Engine/`:

```bash
dotnet restore XE-Local-AI-Engine.slnx
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build
```

The release cannot continue if any restore, build, analyzer, schema, or test step fails. Stop, record the failure, propose a fix, and rerun validation only after the fix is approved and applied.

### 3. Publish artifacts

Publish runtime binaries with explicit RIDs and Release configuration. Expected publish targets are:

```bash
# Windows HostAgent
dotnet publish XE-Local-AI-Engine.HostAgent.Windows/XE-Local-AI-Engine.HostAgent.Windows.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true

# Tray for Windows
dotnet publish XE-Local-AI-Engine.Tray/XE-Local-AI-Engine.Tray.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true

# Linux HostAgent
dotnet publish XE-Local-AI-Engine.HostAgent.Linux/XE-Local-AI-Engine.HostAgent.Linux.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true

# Tray for Linux
dotnet publish XE-Local-AI-Engine.Tray/XE-Local-AI-Engine.Tray.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true
```

Also publish/build the Node Web Server container image and record the immutable image digest that will be used in the runtime manifest.

### 4. Package artifacts

Windows and Linux installer packaging are release-blocking artifacts:

- **Windows:** MSI must contain HostAgent.Windows, the Tray, rootfs tarball, scripts, manifests, icons, and normal/log-mode shortcuts. The MSI may run elevated once for install-time Windows-admin work, but it must exit with no process running.
- **Linux:** deb/rpm must contain HostAgent.Linux, the Tray, systemd user-unit template, desktop launchers, icons, static Docker/rootless-extra tarballs, scripts, and manifests. It must not start user services during `postinst`, enable linger, or create autostart entries.

Record SHA-256 values for packages, rootfs tarballs, static Docker tarballs, and container images. Keep checksums with the release notes.

## Release test matrix

Use this matrix before declaring a release candidate complete.

| Area | Required evidence |
| --- | --- |
| Build | Restore/build/test commands pass for the XE subtree. |
| Schema | Generated schemas are current; manifest samples validate. |
| Image pinning | Runtime manifests use `repo:tag@sha256:digest`; no `:latest`. |
| Platform boundary | No unintended platform files changed. Existing `WorkerHub` regression tests pass. |
| gRPC auth | Missing/expired bearer, tampered body, and replay are rejected. |
| UDS permissions | Non-shared-group process cannot access the HostAgent socket. |
| Admin HTTP | Loopback-only, token-required, safe `Host`/`Origin`, no token logging. |
| Bootstrap model | `bootstrapModelReady=true` before the Web Server connects to `WorkerHub`. |
| Stop/Start | `Stop Services` drains in-flight invocations and `Start Services` restores runtime without re-pulling cached images. |
| Tray UX | Normal mode, log mode, single-instance behavior, icon states, and menu transitions work. |
| Autostart guard | No Windows service, scheduled task, Run key, or XDG autostart entry. |
| BYO mode | HostAgent does not manage containers; Web Server points to external endpoint; platform connection still works. |

## Clean-install release tests

Clean-install runner entry points are tracked in `ci/host-agent/`. Before claiming documentation/release completion, run those scripts against RC artifacts on clean runners and replace the pending transcript sections in the install docs with captured evidence.

### Windows clean install

Runner entry point:

```powershell
pwsh .\ci\host-agent\windows-clean-install.ps1 `
  -MsiPath .\artifacts\XE-Local-AI-Engine.msi `
  -TranscriptPath .\artifacts\windows-clean-install.transcript.txt `
  -ExpectedSha256 <msi-sha256> `
  -RequireTrustedSignature
```

Optional flags: use `-TimeoutSeconds <seconds>` to adjust the install budget, and use `-AllowRebootRequired` only when the RC plan explicitly accepts MSI exit code `3010` as a blocked/reboot-required state.

Expected evidence:

1. Clean Windows 11 image starts without WSL pre-installed.
2. MSI exits with code `0` after elevated install.
3. No service, scheduled task, Run key, or process is created after MSI exit.
4. User launches the desktop shortcut.
5. Within the release time budget, HostAgent reports `state=running` and `desired_state=running`.
6. Ollama and the Web Server are healthy.
7. `bootstrapModelReady=true`.
8. Web Server connects to `WorkerHub`.
9. Tray icon turns green.
10. `Open Web UI` opens the React Web UI URL.

### Linux clean install

Runner entry point:

```bash
bash ci/host-agent/linux-clean-install.sh \
  --package ./artifacts/xe-local-ai-engine.deb \
  --transcript ./artifacts/linux-clean-install.transcript.txt \
  --expected-sha256 <package-sha256> \
  --require-package-signature
```

Optional flags: use `--timeout-seconds <seconds>` to adjust the install budget. For `.deb` packages, use `--allow-apt-deb-install` only when the clean-runner contract allows dependency resolution from configured apt repositories instead of strict `dpkg -i` installation.

Expected evidence:

1. Clean Ubuntu/Debian supported image installs the package successfully.
2. The package does not enable linger, enable the user unit, start user services, or create XDG autostart files.
3. User launches the application menu entry/desktop launcher.
4. `systemctl --user is-active xe-host-agent.service` reports `active` for the desktop user.
5. HostAgent reports `state=running` and `desired_state=running`.
6. Ollama and the Web Server are healthy.
7. `bootstrapModelReady=true`.
8. Web Server connects to `WorkerHub`.
9. Tray icon turns green.

## Runtime smoke tests

After install, validate the runtime from the user's point of view:

1. Launch `XE-Local-AI-Engine` normal mode.
2. Confirm the Tray reaches green or clearly reports a degraded state.
3. Open the React Web UI from the Tray.
4. Confirm substrate status, container status, capabilities, manifest view, and logs render.
5. Confirm the bootstrap model is present.
6. Run one chat/invocation using the bootstrap model.
7. Pull an on-demand model from the React Web UI and confirm progress is shown.
8. Use `Stop Services`; confirm the icon turns gray and the platform sees the node as offline through existing behavior.
9. Use `Start Services`; confirm the runtime returns to green and `WorkerHub` reconnects.
10. Use log mode for an early-boot diagnostic pass and confirm closing the log console does not stop HostAgent.

## Release notes template

Each release should include:

```markdown
# XE Local AI Engine Node Runtime <version>

## Summary
- Runtime mode(s): Windows managed / Linux native / BYO
- Key changes:

## Artifacts
- Windows MSI: <name> — SHA-256: <hash>
- Linux deb/rpm: <name> — SHA-256: <hash>
- Node Web Server image: <repo>:<tag>@sha256:<digest>
- Ollama image: <repo>:<tag>@sha256:<digest>
- Rootfs tarball: <name> — SHA-256: <hash>
- Docker static tarballs: <name> — SHA-256: <hash>

## Validation evidence
- Restore/build/test: <link or transcript>
- Schema/manifest validation: <link or transcript>
- Windows clean install: <link or transcript>
- Linux clean install: <link or transcript>
- Runtime smoke test: <link or transcript>

## Known limitations
- Large/default models pull on demand and can exceed first-call platform timeout.
- No boot/logon autostart by design.
- Fedora/SELinux support is post-MVP.

## Upgrade notes
- Operator action required:
- Config or manifest changes:
- Rollback path:
```

## Troubleshooting during release validation

- Use [Troubleshooting](./troubleshooting.md) for symptom-based triage.
- Prefer status summaries and token generation IDs over raw token values.
- Redact bearer tokens, HMAC secrets, worker credentials, and cloud-provider credentials from transcripts.
- If a clean stop creates new DLQ entries, treat it as a regression in the shutdown-drain contract.
- If the platform needs new runtime fields, stop and create a platform-side follow-up plan; do not smuggle new fields through this node release.

## Future maintenance notes

Track these as separate `(planned)` follow-ups when they become active:

- Auto-update channel for HostAgent and Tray.
- Optional model pre-bake into the installer/rootfs.
- Additional provider implementations behind `ILocalModelProvider`.
- Platform-side runtime field surfacing, if product requires it.
- Fedora/SELinux support.
- Stronger sandboxing or alternative isolation.
- Nightly FakeOllama parity and real Ollama contract checks.

Update this guide whenever release commands, artifact names, installer shape, validation commands, or user lifecycle behavior changes.
