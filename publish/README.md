# XE Local AI Engine distribution and release guide

The official distribution is a **Velopack-managed portable release** for Windows x64 and Linux x64. There is no
system installer, MSI, DEB, RPM, or `Setup.exe`.

The canonical release path is [`.github/workflows/release.yml`](../.github/workflows/release.yml). The workflow
publishes to this repository's GitHub Releases page. It does not publish to a separate tester repository and does not
require a maintainer PAT.

## Official artifacts

| Platform | User-facing artifact | Install model | Self-update |
|---|---|---|---|
| Windows x64 | Velopack `Portable.zip` | Extract to a writable local directory and run the top-level `XE-Local-AI-Engine.exe` launcher | Yes |
| Linux x64 | Velopack `.AppImage` | Mark executable and run the AppImage | Yes; Velopack replaces the AppImage in place |

Windows packing passes `--noInst` to Velopack 1.2.0, so the release must contain exactly one Windows
`Portable.zip` and no `Setup.exe`. Linux packing produces an AppImage, not a ZIP.

Each release also contains the Velopack feed and full/delta package assets used by the updater, plus:

- `CHECKSUMS.sha256` — SHA-256 checksums generated from the verified remote release bytes.
- `RELEASE-MANIFEST.json` — the release tag, source commit, asset sizes and SHA-256 values, and signing state.
- `RELEASE.spdx.json` — a detached SPDX 2.2 release envelope.

The published payload also contains its own SPDX manifest and bundled dependency-license disclosures.

## Version and tag contract

[`eng/ReleaseVersion.props`](../eng/ReleaseVersion.props) is the single release-identity source. It currently composes
`VersionPrefix` and `VersionSuffix` as `0.1.0-rc.5.2`. `Directory.Build.props` imports that file for builds.

`v0.1.0-rc.5.1` already identifies historical commit `a29224eb5f3ab07129c02874dd02b44b91a4cc13` and that version
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

Two independent selectors are involved:

- **Release track:** `main` follows stable releases; `tester` can also see release candidates.
- **OS channel:** Velopack selects the Windows or Linux feed from the package metadata.

The track does not override the OS channel. Both build flavors read the public repository anonymously; users do not
need a GitHub account, device login, token, or repository invitation to check for updates.

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

For Windows, use the `win-x64` publish profile. Raw `dotnet publish` output is not a Velopack package and must not be
described as an official self-updating release.

## Legacy manual packagers

`publish/package-tester-win.ps1` and `publish/package-rc.sh` are **deprecated, reference-only** scripts retained for
historical analysis and static validation. They target superseded distribution flows and are not publication
alternatives. `scripts/lint-release-scripts.sh` still analyzes them to prevent silent script decay.

Do not use their private tester-repository, GitHub App, manual-draft, or Linux-ZIP instructions as the current release
contract.

## Launcher sources

The launcher and cleanup sources remain under:

```text
publish/windows/
publish/linux/
```

They support local/direct publish layouts. The official Windows Velopack portable bundle exposes its own top-level
launcher; the official Linux artifact is the AppImage itself.
