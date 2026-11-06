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
- Windows Smart App Control in enforce mode, or an organization's application
  control (WDAC) policy, can refuse to load an unsigned file whose hash has no
  reputation yet. This has been seen on a Windows tester install with a shipped
  third-party DLL (a Quartz assembly), and an update that replaces a file can
  trigger it again. The engine does not crash on it: it names the blocked file
  on stderr (`Application control blocked: <file>. ...`), logs it (or writes
  `startup-crash.log` when the log is not open yet) and exits with code 11;
  the desktop app shows a dialog naming the file and the install folder. Only
  signing removes the cause. Until then a user can turn Smart App Control off
  in Windows Security under "App & browser control", or retry later once the
  file's hash has gained reputation; a managed policy needs the organization's
  IT administrators to allow the install folder.

## Forward gate

A signing certificate may be introduced in a later release. Introducing signing
does **not** retroactively validate this or any earlier unsigned artifact; the
signing decision and its evidence must be recorded separately when that change
is made.
