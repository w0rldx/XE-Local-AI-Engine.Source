# How to download the app from GitHub

The ready-to-run application is on the repository's **Releases** page, not behind the green **Code** button.

## Step 1 — Open the latest release

The repository and its release assets are public. You do not need to sign in to GitHub.

[**Open the Releases page →**](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases)

## Step 2 — Expand Assets

Under the release notes, expand **Assets**. Choose the platform artifact:

| Platform | Download |
|---|---|
| Windows x64 | `XE-Local-AI-Engine-win-Portable.zip` |
| Linux x64 | The file whose name ends in `.AppImage` |

Also download `CHECKSUMS.sha256`. The `.nupkg` files and `releases.win.json` / `releases.linux.json` are update-feed
assets used by Velopack; users do not open them manually. GitHub's automatic **Source code** archives are not packaged
applications.

## Step 3 — Keep the unsigned download if you trust it

The project does not yet have a signing certificate, so current release artifacts are unsigned. Signing is planned.

- Edge or Chrome may say the Windows ZIP is not commonly downloaded. Open the downloads list, choose **Keep**, then
  **Keep anyway** if you trust the release.
- Firefox may require **Allow download**.
- Linux desktop environments or security tools may require an explicit trust/execute action for the AppImage.

Verify the checksum before running either artifact.

## Step 4 — Verify SHA-256

### Windows

Open PowerShell in the download directory:

```powershell
Get-FileHash .\XE-Local-AI-Engine-win-Portable.zip -Algorithm SHA256
Select-String -Path .\CHECKSUMS.sha256 -Pattern 'XE-Local-AI-Engine-win-Portable.zip'
```

The computed hash must match the hash at the start of the checksum line, ignoring letter case. If it does not match,
do not run the file.

### Linux

Run this in the download directory:

```bash
grep -E '  \./.*\.AppImage$' CHECKSUMS.sha256 | sha256sum --check -
```

The command must print `OK`.

`RELEASE-MANIFEST.json` binds the asset inventory to the source tag and commit. `RELEASE.spdx.json` provides the
detached SPDX release inventory.

## Next

- [Install on Windows](install-windows.md)
- [Install on Linux](install-linux.md)

**[← Back to the main page](../README.md)**
