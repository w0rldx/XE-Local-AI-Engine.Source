# XE Local AI Engine distribution and release guide

The official distribution is a **Velopack-managed portable release** for Windows x64 and Linux x64. There is no
system installer, MSI, DEB, RPM, or `Setup.exe`.

The canonical release path is [`.github/workflows/release.yml`](../.github/workflows/release.yml). The workflow
publishes to this repository's GitHub Releases page. It does not publish to a separate tester repository and does not
require a maintainer PAT.

## Official artifacts

| Platform | User-facing artifact | Install model | Self-update |
|---|---|---|---|
| Windows x64 | Velopack `Portable.zip` | Install ASP.NET Core Runtime 10.0.12+ (x64) and WebView2 Evergreen Runtime, extract to a writable directory, and run the top-level launcher | Yes |
| Linux x64 | Velopack `.AppImage` | Mark executable and run the AppImage | Yes; Velopack replaces the AppImage in place |

Windows packing passes `--noInst` to Velopack 1.2.0, so the release must contain exactly one Windows
`Portable.zip` and no `Setup.exe`. Linux packing produces an AppImage, not a ZIP.

The Windows payload is framework-dependent. `XE-Local-AI-Engine.Client` publishes as DLL/deps/runtimeconfig files with
no apphost or runtime; `XE-Local-AI-Engine.WindowsLauncher` publishes a small C# apphost into the same directory. The
launcher validates the payload and installed ASP.NET Core runtime before starting the native Desktop shell through `dotnet.exe`.
The shell owns its engine process and offers Exit or minimize-to-tray on first close. `--browser` starts the standalone
local engine and opens a browser; `--headless` starts the same local engine without UI. Operator commands and
`--mcp-only` bypass the shell. Updates preserve the selected mode. WebView2 is a separately installed prerequisite;
XE does not silently download its installer. Linux packages launch the native shell and retain a standalone engine payload. Native Ubuntu LTS
X11/Wayland acceptance remains unvalidated: real Ubuntu testing was waived, not established by Windows or WSL checks.
If the base .NET runtime is absent, Microsoft's apphost reports the missing framework; if ASP.NET Core is absent or too
old, the launcher prints the exact requirement and opens the official .NET 10 download page.
The archive must not contain `coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`, `System.Private.CoreLib.dll`, or the .NET
Library License. The Linux AppImage remains self-contained. Trimming stays **off** for both profiles
(`PublishTrimmed=false`, reasoning in the `.pubxml` comments): the application is reflection-heavy.

Each release also contains the Velopack feed and full/delta package assets used by the updater, plus:

- `CHECKSUMS.sha256` — SHA-256 checksums generated from the verified remote release bytes.
- `RELEASE-MANIFEST.json` — the release tag, source commit, asset sizes and SHA-256 values, and signing state.
- `RELEASE.spdx.json` — a detached SPDX 2.2 release envelope.

The published payload also contains its own SPDX manifest and bundled dependency-license disclosures.

## Version and tag contract

[`eng/ReleaseVersion.props`](../eng/ReleaseVersion.props) is the single release-identity source. It currently composes
`VersionPrefix` and `VersionSuffix` as `1.0.0-rc.2`. `Directory.Build.props` imports that file for builds.

`v0.1.0-rc.5.1` already identifies historical commit `cc37f1a588c31cf2ad088c9d75bae2fb637a3234` and that version
was published through the retired tester flow. Do not move or reuse the tag. Any public release containing later
changes needs a new version and matching immutable tag.

To cut a release:

1. Update `VersionPrefix` and `VersionSuffix` in `eng/ReleaseVersion.props`.
2. Update [`CHANGELOG.md`](../CHANGELOG.md).
3. Commit the release identity and notes.
4. Create and push the immutable matching tag, `v<version>`, on that commit.

The workflow rejects a manual run that is not bound to an existing `v*` tag, a tag that does not match the composed
version, or a tag that does not resolve to the checked-out commit. A version string is single-use.

## Release workflow

The release workflow runs these stages in order:

1. **Validate** — reuse `.github/workflows/build-and-test.yml` against the tagged source.
2. **Bind version and source** — verify SemVer, exact tag spelling, and source commit; generate release notes with a
   checksum-pinned `git-cliff`.
3. **Build and pack** — matrix jobs build Windows and Linux assets only. They run a frozen frontend install, license
   validation, the production build, `dotnet publish`, payload SPDX generation/validation, Velopack 1.2.0 packing,
   final artifact-content validation, and retained-artifact hashing.
4. **Prepare the draft serially (protected)** — `prepare-release-draft`, guarded by the externally configured
   `open-source-release` environment, verifies retained hashes, creates one
   Velopack draft, merges both OS channels into it, and downloads the
   draft assets again for remote-byte verification.
5. **Attach detached evidence (same protected preparation)** — the same preparation job generates and uploads
   `RELEASE.spdx.json`, `RELEASE-MANIFEST.json`, and `CHECKSUMS.sha256` from the verified remote bytes, then
   re-downloads and verifies the complete draft.
6. **Promote the verified draft (separately protected)** — `publish-release` requires a second approval through the
   same environment, runs the release-authority and unsigned-risk gate, re-verifies the exact draft, and publishes it
   without rebuilding, re-uploading, or replacing assets. It then confirms the public release and both OS feeds
   anonymously.

Matrix jobs never publish independently. Serialization prevents the two Velopack channels from racing or creating
separate releases.

The pinned Microsoft SBOM tool targets .NET 8. Release CI therefore installs a supported .NET 8 runtime alongside
the repository's .NET 10 SDK. Do not force the tool to roll forward to .NET 10: its component detector can return
success with an incomplete package inventory. `scripts/compliance/sbom-tool.sh` fails closed when .NET 8 is absent.

## Update channels

Users choose the update channel in the app (About page), and the choice is node state that survives updates
([ADR 0014](../docs/adr/0014-update-channels-and-development-builds.md)):

- **Stable** reads the OS feed (`win` / `linux`) for released versions only.
- **Preview** reads the same feed and also accepts release candidates.
- **Development** additionally reads the `win-dev` / `linux-dev` feed, which only Development builds publish.

The baked packaging flavour (`-p:UpdateChannel=main|tester|dev`) is internal: it only sets the default channel a node
follows until its operator picks one (`main` = Stable, `tester` = Preview, `dev` = Development). Velopack's OS
channel still comes from the package metadata, and no channel ever offers a lower version. Every flavour reads the
public repository anonymously; users do not need a GitHub account, device login, token, or repository invitation to
check for updates.

### Development builds

[`.github/workflows/dev-build.yml`](../.github/workflows/dev-build.yml) runs daily (03:17 UTC, or by manual dispatch
on `develop`) and skips an unchanged `develop` tip. It packages through the same
[`package-velopack.yml`](../.github/workflows/package-velopack.yml) as a tag release with `UpdateChannel=dev`, and
publishes one GitHub **prerelease** tagged `dev/<version>` carrying only `releases.win-dev.json` and
`releases.linux-dev.json`, so Stable and Preview feed reads skip it. The version extends the newest reachable `v*`
tag (`1.0.0-rc.2` becomes `1.0.0-rc.2.dev.<yyyymmdd>.<n>`; a stable anchor bumps the patch). There is no
`open-source-release` approval gate; every technical gate of a tag release still runs. After a successful publish
the workflow prunes all but the newest 30 Development releases and keeps their tags.

## Signing and verification

Release artifacts are currently **unsigned because the project does not yet have a signing certificate**. Certificate
signing is planned. Until then:

- Windows may show browser reputation warnings and Microsoft Defender SmartScreen's **Unknown publisher** warning.
- Linux desktop environments or endpoint-security tools may require an explicit trust/execute action for a newly
  downloaded AppImage.
- Users should download `CHECKSUMS.sha256` with the platform artifact and verify the SHA-256 value before running it.
- `RELEASE-MANIFEST.json` binds the published assets to the tag and source commit; `RELEASE.spdx.json` records the
  detached release inventory.

Publication is fail-closed on the approved, current release-authority and unsigned-risk record. That gate documents
the accepted interim risk; it does not make unsigned binaries equivalent to signed binaries.

## Local publish output

Build the React application before a direct `dotnet publish`; the publish target rejects a missing `dist/index.html`.

```bash
(
  cd XE-Local-AI-Engine.Client.React
  pnpm install --frozen-lockfile
  pnpm run build
)

dotnet publish XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj \
  --configuration Release \
  -p:PublishProfile=linux-x64 \
  -p:UpdateChannel=main
```

For a direct Windows payload, publish both projects into the same output directory:

```bash
WIN_OUT="$PWD/.tmp/publish/win-x64"
dotnet publish XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj \
  --configuration Release -p:PublishProfile=win-x64 -p:UpdateChannel=main --output "$WIN_OUT"
dotnet publish XE-Local-AI-Engine.WindowsLauncher/XE-Local-AI-Engine.WindowsLauncher.csproj \
  --configuration Release -p:PublishProfile=win-x64 --output "$WIN_OUT"
```

Raw `dotnet publish` output is not a Velopack package and must not be described as an official self-updating release.

## Legacy manual packagers

`publish/package-tester-win.ps1` and `publish/package-rc.sh` are **deprecated, reference-only** scripts retained for
historical analysis and static validation. They target superseded distribution flows and are not publication
alternatives. `scripts/lint-release-scripts.sh` still analyzes them to prevent silent script decay.

Do not use their private tester-repository, GitHub App, manual-draft, or Linux-ZIP instructions as the current release
contract. A `win-x64` zip produced by `package-rc.sh` is cross-built on Linux: native-library self-extraction,
console-close child cleanup and browser auto-open cannot be verified off-Windows, so smoke-test it on real Windows before
handing it to anyone.

## Legacy launcher sources

The launcher and cleanup sources remain under:

```text
publish/windows/
publish/linux/
```

They support deprecated manual publish layouts. The official Windows payload uses the C# project under
`XE-Local-AI-Engine.WindowsLauncher/`; Velopack exposes that apphost through its top-level launcher. The official Linux
artifact is the AppImage itself.
