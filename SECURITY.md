# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull requests.**

Report privately through GitHub's built-in flow:

1. Go to the repository's **Security** tab → **Report a vulnerability** ([Privately reporting a security vulnerability](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)).
2. Describe the issue, the impact, and steps to reproduce.

This opens a private security advisory visible only to you and the maintainer.

Please include, where you can:

- The type of issue and the component/file involved.
- Steps to reproduce, or a proof of concept.
- The impact — what an attacker could do.
- Any suggested remediation.

## Scope

XE Local AI Engine is a local-first desktop application: it runs a local web server on loopback, supervises local inference runtimes, and stores data in a per-user directory. Areas of particular interest:

- The local admin API surface (`/api/local/v1`) and its loopback/`Host`/`Origin` guarding.
- Handling of secrets at rest (node key, Data Protection key ring, encrypted columns/blobs).
- The agent process/container sandbox and Development Mode's code-execution boundary.
- Model/skill import and any path-traversal or untrusted-content handling.

## What to expect

This is an early-stage project maintained by one person in their spare time, so responses are best-effort rather than bound to a formal SLA. You will get an acknowledgement, and coordinated disclosure once a fix (or a documented mitigation) is available. Please give a reasonable window before any public disclosure.

## Known posture (not vulnerabilities to report)

- Release binaries are currently **unsigned because the project does not yet have a signing certificate**. Windows
  can show browser reputation and SmartScreen **Unknown publisher** warnings; Linux desktop environments or security
  tools can require an explicit trust/execute action for the AppImage. Certificate signing is planned.
- Official releases publish `CHECKSUMS.sha256`, `RELEASE-MANIFEST.json`, and `RELEASE.spdx.json`. Verify the platform
  artifact's SHA-256 before running it. Checksums and SPDX inventory improve integrity/transparency but do not provide
  publisher authentication equivalent to a trusted code signature.
- Development Mode executes repository code on your machine by design; treat it as running the target repo's own build/test commands. See the user guide's privacy/security notes.
