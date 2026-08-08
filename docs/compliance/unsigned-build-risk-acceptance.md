# Unsigned portable-build risk acceptance

The official portable artifacts (Windows Velopack `Portable.zip` and Linux
AppImage) are published **without a code-signing certificate** for this release.

## Accepted risk

The project owner (`w0rldx`), as the party authorized to publish these
binaries, knowingly accepts the consequences of unsigned delivery:

- Windows SmartScreen and some antivirus products may warn on first run or
  quarantine the executable until the user explicitly permits it.
- Users cannot cryptographically verify the publisher identity from the OS
  signature; they rely on the published SHA-256 checksums and the release
  provenance instead.

## Forward gate

A signing certificate may be introduced in a later release. Introducing signing
does **not** retroactively validate this or any earlier unsigned artifact; the
signing decision and its evidence must be recorded separately when that change
is made.
