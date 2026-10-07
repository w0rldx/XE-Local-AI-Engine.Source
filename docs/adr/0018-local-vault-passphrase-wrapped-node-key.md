# ADR 0018: The persisted node key is a random master key wrapped by the admin password and a recovery code, and a locked node serves only an unlock page

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-10-07; the design is implemented on `develop`.
- **Date:** 2026-10-01
- **Scope:** How the packaged desktop and standalone local modes keep the root secret of the node (the SQLite column
  key, the JWT signing key and the non-Windows Data Protection key-ring KEK) at rest: the on-disk format of
  `node.key`, who can unwrap it, what the engine does before it is unwrapped, and how a forgotten password is
  recovered. It changes nothing about the three HKDF derivations, the Data Protection key ring, any existing
  ciphertext, or the operator-supplied secret sources used by development, CI, tests and containers.
- **Authority:** Operator decisions of 2026-10-01 (mandatory vault with a one-time legacy auto-wrap, recovery code,
  BCL PBKDF2, whole-host wait, one password for two stores, persisted `node.key` only), recorded in
  `Plans/vault-key-custody-2026-10-01/PLAN.md` §0 (the plan directory is git-ignored; it is the record of the
  decisions, not a published document).
- **Amends:** nothing. It narrows one statement in [Security & Privacy](../wiki/12-security-and-privacy.md) §2.1: a
  persisted desktop `node.key` is no longer the raw secret (Linux: guarded by `0600` alone; Windows: DPAPI).

## Context

In packaged desktop and standalone local modes `DesktopBootstrap` generated a 32-byte operator secret on first
launch and persisted it to `node.key` inside the per-user data directory, next to `node.sqlite`, the uploaded-file
blobs and the Data Protection key ring that the secret protects. On Linux the file was the raw bytes guarded only by
`0600`; on Windows it was DPAPI `CurrentUser`-wrapped. A copy of the data directory (a backup, a synced folder, a
disk image, a misconfigured share) therefore carried the key with the data, and on Linux even a read of the directory
by the same account's other tooling was enough.

The key cannot simply move into an OS keyring: the node also runs headless, in MCP-only mode and on servers without
a user session, and the product wants one custody story that behaves the same on both supported operating systems.
The admin already has a password. Deriving a key-encryption key from it separates the key from the data without
adding a second secret to remember, at the price that something must ask for the password before the node can open
its own database.

That price is the real decision. The node is a long-running process with unattended work: the scheduler, the inbound
MCP server, the integration API, retention and recovery services. Each of them touches encrypted columns. A design in
which the host starts and only "the encrypted parts" wait would have to prove, feature by feature, that nothing
reads a protected value while locked, and would leave a half-alive node that answers some routes.

## Decision

1. **The operator secret is unchanged; only its custody changes.** The persisted `node.key` holds the existing
   32-byte operator secret as a random master key. On migration its bytes are kept, so nothing already encrypted is
   re-encrypted. The three HKDF derivations and their distinct `info` strings stay as documented in
   [Security & Privacy](../wiki/12-security-and-privacy.md) §2.1.
2. **`node.key` becomes a versioned JSON file (format v2) that never contains the raw secret.** It starts with
   `{"magic":"xe-vault","v":2` and holds two independent wraps of the master key, both AES-256-GCM through the single
   `AesGcmNodeAeadCipher` owner with distinct associated data per wrap: a password wrap whose KEK is
   PBKDF2-SHA512 (`Rfc2898DeriveBytes.Pbkdf2`, iteration count and salt stored in the file, so an unwrap honours the
   value it was written with), and a recovery wrap whose KEK is HKDF-SHA256 over a recovery code. The recovery code is
   25 CSPRNG bytes rendered as RFC 4648 base32 in 8 dash-separated groups of 5 characters, shown once and never
   stored. The file name, `0600` atomic temp-and-move writes and the sensitive-file exclusions are unchanged. A file
   that does not start with `{` is the legacy raw or DPAPI form.
3. **The vault is mandatory; a legacy `node.key` is wrapped once, automatically.** There is no maintained legacy mode.
   A fresh install creates the vault when the first admin is set up. An existing install with a legacy key boots
   unlocked in a `pending` state and the SPA forces a "confirm your password" step that verifies the password through
   Identity, wraps the existing key and shows the recovery code. The Windows DPAPI layer is dropped for v2: the
   password wrap is the at-rest protection. A legacy DPAPI blob is still unwrapped once, for the migration.
4. **One password, two stores.** The vault passphrase is the admin login password. Identity keeps its hash and the
   vault keeps its wrap; first-run setup, change-password and reset update both, and a failure in either restores the
   other so file and hash never disagree.
5. **A locked node is a pre-host, and the whole host waits.** When `node.key` is a v2 file the process starts a
   minimal second web host on the real origin before building the real one. It serves the SPA, readiness, an
   `auth/status` that reports the vault state, and two anonymous loopback-only unlock routes (password and recovery
   code), rate limited, with no failure answered faster than a fixed minimum. Every other `/api/local/v1` route
   answers 503. On a successful unlock the pre-host stops, the real host is built with the unwrapped secret handed over
   as an in-memory configuration value, and it re-binds the same origin when the port is free again. No unattended
   feature runs while the node is locked: the scheduler, the inbound MCP server, the integration API, retention and
   recovery services do not exist until the real host does.
6. **The locked node still reports itself as running.** The pre-host publishes the port file, `ready.json` and the
   `XE_READY` line itself, so the desktop shell's attach-or-own logic, the 2-minute start deadline, `--status --json`
   and the single-instance lease behave as for any running engine. The instance lease is taken before the pre-host, so
   a second launch exits with code 4 rather than serving a second unlock page.
7. **Operator-supplied secrets are out of scope.** `XE_NODE_SQLITE_KEY`, `/run/secrets/node-sqlite-key` and the
   Aspire `Parameters:node-sqlite-key` stay operator custody and unwrapped; the vault is not created or consulted when
   one of them supplies the secret. The development loop, CI, the test fixtures and containers are unchanged.
8. **CLI one-shots unlock from what they are given and never serve; a serve start may unlock the same way.** On a locked vault `--setup` and `--mcp-key` read
   the password from `XE_ADMIN_PASSWORD` or `--admin-password-stdin`; `--reset-admin-password` requires the recovery
   code on stdin with `--recovery-code-stdin`. A missing or wrong secret exits with code 5 and starts no host. A host started in a serve mode with the password in `XE_ADMIN_PASSWORD` or on stdin skips the unlock page and unlocks directly; a wrong password exits 5. An
   install that has lost both the password and the recovery code has lost its data; the reveal screen, the installers
   and the docs say so.
9. **Every restart is a locked start.** A self-update restart launches a fresh process and lands on the unlock page.
   This is inherent to decision 5 and is accepted.
10. **Same-user malware while the node is unlocked is out of scope.** Once the real host holds the secret in memory, a
    process running as the same user can read it as it can read the node's memory today. The vault protects the key
    at rest and across copies of the data directory, not a compromised live session.

## Consequences

- **The node needs a human after every start.** An unattended reboot, an autostart entry or a service restart brings
  up a locked node whose scheduled jobs, inbound MCP and integration API do nothing until someone enters the
  password. A serve-mode start given `XE_ADMIN_PASSWORD` or `--admin-password-stdin` unlocks non-interactively, which
  is how the installers' `--start` brings a headless install up unlocked; without it the node serves the unlock page and
  MCP answers 503. Autostart units carry no credentials, so an autostarted node waits for the browser. Operators who need
  fully unattended starts keep using an operator-supplied secret (decision 7).
- **There are two ways to lose everything.** Forgetting the password and losing the recovery code leaves the key
  unrecoverable; no remote or support path exists, by design.
- **A pre-host is a second web host in the startup path.** It duplicates a small slice of the pipeline (security
  middleware, document policy, response headers, readiness) and must be kept in step with the real host; shared
  helpers for the response headers and the ready publication keep the two byte-identical. The origin can change if
  the port is lost between the two hosts, in which case the browser's origin-scoped preferences reset; that is logged.
- **Any test or script that spawns the real host against a data directory with a v2 key must unlock it.** Without
  `XE_ADMIN_PASSWORD`, `--admin-password-stdin` or an operator-supplied secret, the host waits on the unlock page
  until the shell's start deadline.
- **The recovery code is a second credential that can open the key.** It is shown once, with the same weight as the
  password, and is never written by the engine or the installers.
- **Password changes carry a short window.** The vault is re-wrapped before Identity changes the hash, and restored
  if Identity refuses; a crash inside that window is the one case where file and hash can differ, and recovery is by
  the recovery code.
- **Windows keeps DPAPI for the Data Protection key ring**, which is not part of this change. Only `node.key` loses its
  DPAPI layer.
- **Windows acceptance is owed.** The legacy DPAPI-to-v2 migration and the unlock page inside the shell have been
  exercised on Linux only; the Windows runbook section covers the check.

## Alternatives considered

- **Argon2id instead of PBKDF2.** Stronger against GPU cracking, but the BCL has no Argon2 and it would add a native or
  third-party dependency to a security root. Rejected for now: PBKDF2-SHA512 with a stored, tunable iteration count
  needs no dependency, and the file format can move to another KDF behind its version field.
- **OS keyring (DPAPI, Secret Service, Keychain) as the custodian.** No password prompt, but there is no portable story
  for headless and server hosts, the two operating systems would behave differently, and the key would follow the OS
  account rather than the admin's credential. Rejected as the only mechanism; it remains open as a later opt-in.
- **The host starts and only encrypted features lock.** Keeps the node answering, but every code path that can touch a
  protected column would need to be proven unreachable while locked, and a half-alive node invites exactly the
  unattended access this decision exists to stop. Rejected for the whole-host wait.
- **An opt-in vault with the legacy format maintained.** Safer for upgraders, but the product is pre-1.0, a maintained
  legacy mode doubles the key-custody code and tests, and most installs would never opt in. Rejected for a mandatory
  vault with a one-time automatic wrap.
- **A separate vault passphrase.** Cleaner key separation, but a second secret to remember for a single-admin node, and
  a password change would still have to touch both. Rejected for one password, two stores.
- **Re-encrypt the data under a new key at migration.** Makes the master key independent of the legacy secret, but
  rewrites every encrypted column and the Data Protection ring in one risky step. Rejected: the legacy bytes become the
  master key, and rotating it remains a separate, explicit decision.
