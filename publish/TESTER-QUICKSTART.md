# XE Local AI Engine tester quickstart

This file previously described the retired private tester-repository and manual packaging flow. Those instructions
are no longer valid and have been removed.

Use the current public documentation:

- [User guide](../docs/user-guide/README.md)
- [Download from GitHub](../docs/user-guide/docs/download-from-github.md)
- [Windows installation](../docs/user-guide/docs/install-windows.md)
- [Linux installation](../docs/user-guide/docs/install-linux.md)
- [Updating](../docs/user-guide/docs/updating.md)

Official tester binaries are public Velopack-managed portable artifacts. Windows uses a `Portable.zip` with no
`Setup.exe`; Linux uses an AppImage. Both self-update anonymously. Verify `CHECKSUMS.sha256` before running either
unsigned artifact.

Maintainers should use the canonical [distribution and release guide](README.md). The retained
`package-tester-win.ps1` and `package-rc.sh` scripts are deprecated, reference-only material, not release paths.
