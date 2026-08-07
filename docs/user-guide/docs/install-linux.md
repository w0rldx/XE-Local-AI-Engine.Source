# Installing on Linux

The official Linux x64 build is a **Velopack AppImage**. It is portable, self-contained, and self-updating. There is
no Linux ZIP, DEB, RPM, or system installer.

## Before you start

| | Requirement |
|---|---|
| **Architecture** | x64 only; no ARM build |
| **Distribution** | A current glibc-based Linux distribution |
| **Disk** | At least 5 GB; larger models need much more |
| **GPU** | Optional; CPU works, but a supported GPU is faster |
| **Internet** | Required for the initial runtime/model downloads and update checks |

The application bundles .NET. You do not need to install a .NET runtime.

## Step 1 — Download and verify the AppImage

1. Open the [Releases page](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases).
2. Expand **Assets**.
3. Download the file whose name ends in `.AppImage` and `CHECKSUMS.sha256`.

The feed files and `.nupkg` packages are for the in-app updater. Do not open them manually.

In the download directory, verify the AppImage:

```bash
grep -E '  \./.*\.AppImage$' CHECKSUMS.sha256 | sha256sum --check -
```

The command must print `OK`. If it reports a mismatch, do not run the file.

`RELEASE-MANIFEST.json` and `RELEASE.spdx.json` are also available for release/source and SPDX inventory inspection.

## Step 2 — Put it somewhere writable

Create a local application directory and move the AppImage there:

```bash
mkdir -p ~/Applications
mv ~/Downloads/XE-Local-AI-Engine*.AppImage ~/Applications/
cd ~/Applications
```

Keep the AppImage in a directory your user can write. The updater replaces this file in place. A location under your
home directory avoids elevation; a privileged location may require `pkexec` during an update.

Avoid network shares, synchronized folders, removable media, and filesystems mounted with `noexec`.

## Step 3 — Mark it executable and run it

```bash
chmod +x ./XE-Local-AI-Engine*.AppImage
./XE-Local-AI-Engine*.AppImage
```

The AppImage is the application. Do not unzip or extract it.

Two things happen:

1. The terminal shows application logs. Leave it open; closing it stops the application.
2. Your browser opens on a loopback address such as `http://127.0.0.1:<port>/`.

If the browser does not open, copy the exact `http://127.0.0.1:` address printed in the terminal.

## Unsigned-build warnings

The current artifacts are unsigned because the project does not yet have a signing certificate. Signing is planned.
Linux has no single SmartScreen-equivalent prompt, but your browser, desktop environment, mount policy, or endpoint
security tool may block a newly downloaded executable.

Verify `CHECKSUMS.sha256` before running the file. Then, only if you trust the verified release:

- ensure the file has the executable bit (`chmod +x`),
- ensure its filesystem is not mounted `noexec`, and
- use your desktop environment's **Allow launching** or **Trust and launch** action if it requires one.

## Updating

The official AppImage checks the public release feed anonymously. No GitHub account, device login, or access token is
required.

When an update is available, apply it in the app. Velopack downloads the Linux release and replaces the AppImage. If
the containing directory is privileged, Velopack may invoke `pkexec`; keeping the file in `~/Applications` normally
avoids that prompt.

Your chats, settings, models, and local runtimes live outside the AppImage and remain in place.

See [Updating](updating.md) for release-track and rollback details.

## Stopping the app

Close the terminal or press `Ctrl+C`. Closing only the browser tab does not stop the local server.

Run one application instance at a time against a user-data directory.

## Where your data lives

The default Linux data directory is:

```text
~/.local/share/XE-Local-AI-Engine
```

If `XDG_DATA_HOME` is set, the app uses `$XDG_DATA_HOME/XE-Local-AI-Engine` instead.

## GPU notes

Vulkan is the default Linux GPU path for AMD, Intel, and NVIDIA. Install your distribution's Vulkan ICD and use
`vulkaninfo --summary` to confirm that Vulkan sees the GPU. Without a working GPU backend, the app can fall back to
CPU inference, which is much slower.

NVIDIA users can also build the pinned CUDA runtime from inside the app.

## Next

Continue to [First run](first-run.md).

**[← Back to the main page](../README.md)**
