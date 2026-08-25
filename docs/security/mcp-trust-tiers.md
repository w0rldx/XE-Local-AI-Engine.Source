# MCP server trust tiers

- **Status:** Accepted, implemented in Phase 2 (gap G2) of `Plans/secure-agent-execution-2026-08-25/02-phased-plan.md`.
- **Scope:** Outbound MCP servers this node connects to. The node's own inbound MCP server is out of scope (ADR 0006).
- **Authority:** Operator decision D-C (2026-08-25): the tiers are `Sandboxed` / `PrivilegedHost` / `BuiltInTrusted`.
  There is **no `Remote` tier**; `McpOptions.HttpLoopbackHosts` stays exact-match loopback.

## The three tiers

| Tier | What it may do | How a server gets it |
|---|---|---|
| **Sandboxed** | Runs as a stdio child **inside the substrate**: a mount namespace with no host filesystem, an empty network namespace, a disposable jail as its working directory, and only its configured environment variables. | The **default** for every stdio registration, existing rows included. |
| **PrivilegedHost** | Runs as a plain host child, exactly as every stdio server did before this change — the operator's filesystem, the operator's network. Environment is still scrubbed (`InheritEnvironmentVariables = false`). | Explicit per-server operator opt-in on the registration. Never a fallback, never inferred. |
| **BuiltInTrusted** | Reserved for a transport the engine itself owns. Nothing sets it today. | Engine-owned only: the CRUD surface rejects it, so it cannot be reached from the API or the UI. |

The tier answers "where does this server's process run", so it is **inert for HTTP**: an HTTP server is already
running, this node only opens a loopback socket to it, and the allow-list is the control. An HTTP registration is
normalized to the column default on save and the UI offers no choice — deliberately, so a stored `PrivilegedHost` on a
row this node never launched can never be read as a grant somebody made.

## How a `Sandboxed` server is hosted

The workload is declared once, as an engine-owned constant, per ADR 0007 Decision 1:
`SandboxWorkloads.McpStdio` — `Toolchain = HostToolchain`, `IsolationFloor = Filesystem`, `NetworkFloor = None`,
`RequestsResourceLimits = false`, `Persistence = Disposable`.

**Which backend it resolves to.** `HostToolchain` is what an MCP server needs — `npx`, `uvx`, a compiled binary the
operator installed — so a container backend can never serve it (`SandboxProviderSelector.FindUnmetAxis` rejects on the
toolchain axis before anything else). Minimal-satisfying resolution therefore lands on the **process backend**, and
`SandboxSubstrateSelectionArchitectureTests` asserts that as an enumeration rather than as a comment.

**Why the isolation floor is `Filesystem` and not absent.** ADR 0007 gives the floor no default precisely so that a new
consumer has to argue for its value. The argument here is the abuse case the threat model already records (AB3): an
operator installs a server from a README and its first tool call reads `~/.ssh` and the node database. Environment
scrubbing — the only control this path had — does not touch the filesystem. A server needs its own package files and
nothing else; it does not need the repository, the node database, the operator's home directory or the network. That is
exactly the property `SandboxIsolationMode.Filesystem` names, so the floor is the honest declaration and anything weaker
would be a floor chosen to avoid a refusal rather than to describe a need.

**What the server can see.** The isolated chain's fixed view — a read-only `/usr` (with the usr-merge legacy symlinks),
a synthetic `/etc`, `/proc`, `/dev`, a writable jail at `/work` with `HOME` and `/tmp` beneath it — plus two
engine-derived read-only trees: the real directory of the resolved `Command`, and the configured `WorkingDirectory`
when one is set. Those two are where a stdio server's package files actually live (`node_modules`, a venv, a `dist/`),
and both are already on the registration, so nothing new is asked of the operator and nothing is derived from a
repository. The **working directory is the jail**, not the configured path: the configured path is bound read-only
because a third-party server has no reason to write into the tree it was installed from.

**Neither tree may cover a sensitive host root.** Both go through one gate
(`SandboxedMcpStdioTransport.AddBindableTree`), and a tree that **equals or contains** any of the following is
**refused** — the connection fails, naming the path and the tier, rather than mounting it:

| Denied root | Why |
|---|---|
| the operator's home directory | binding it exposes every credential store beneath it |
| `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config`, `~/.docker`, `~/.kube` | credential and CLI-token stores; `~/.config` is where gcloud, gh and most others keep theirs |
| the node data directory (`INodeDataDirectory.Root`) | the node database, its key material, every sandbox jail, the workspace manifests that are deliberately never mounted |
| the engine's own install directory (`AppContext.BaseDirectory`) | the assemblies doing the sandboxing, and whatever ships beside them |
| `/root`, `/etc`, `/var`, `/` | never a server's package tree, always somebody's credentials or state |

The rule is **equals-or-ancestor, not "is under"**, and that asymmetry is the design. A tree *beneath* one of these is
fine: `~/.nvm/versions/node/v22/bin` exposes a node install, while `$HOME` exposes the operator. Refusing every
subtree of home would make `npx`- and `uvx`-based servers unusable at the default tier, which is how a security
control gets switched off. Both sides are compared after normalization and link resolution, so a symlink to home, a
relative segment or a trailing separator cannot walk past the list. The list is code-owned: a denylist a registration
could edit would be no denylist at all.

**Egress.** `--unshare-net` is unconditional on the isolated chain and is positively controlled by the containment
probe, so `NetworkPolicy = None` is enforced by the same mechanism `run_python` relies on rather than by the separate
`unshare(1)` egress path.

**Fail-closed, and how the operator sees it.** A host whose sandbox provider does not advertise
`SupportsFilesystemIsolation` cannot serve this tier, and the tier does **not** degrade: the connection attempt is
refused before a process is launched. The refusal is engine-authored (it names no host path and no secret) and travels
verbatim on `McpServerConnectionStatus.LastError` to the MCP settings page, naming the tier, the reason, and the two
ways out — install bubblewrap plus the user-namespace support the containment probe reports as missing, or move the
server to `PrivilegedHost` deliberately. This is the one MCP connection error that is not redacted to a generic
string, because a generic string here would be indistinguishable from the server simply being broken.

**Windows.** `HostSandboxContainmentProbe` reports `SandboxContainment.None` on Windows — the Job Object path is not
implemented (G12) — so `Sandboxed` is unavailable there and every stdio server refuses to start with the message above
until G12 lands. That is the visible degradation the phased plan asks for, not a silent one. The
`mcp-stdio` row on the Development status isolation panel reports the same fact ahead of any connection attempt.

**Lifecycle.** The sandbox is created per server registration with an attach key carrying the server id, so two servers
never share a jail. The long-lived child runs through `ISandboxRuntimeProvider.StartInteractiveAsync` and is registered
in the jail's in-flight set, which means the existing teardown covers it unchanged: disposing the MCP connection kills
the transient systemd scope, tree-kills the child and deletes the jail, and a hard kill of the engine leaves a marker
that `SandboxOrphanReaper` sweeps at the next start behind its three gates.

## How `PrivilegedHost` is granted, and what it costs

It is set on the registration, per server, by the operator — a source of authority the model cannot reach. There is no
node-wide switch and no inference from the command; a server that needs host access (a browser-driving server, a server
that manages the operator's own files) is a deliberate decision recorded on the row.

A **stdio** `PrivilegedHost` server's tools are **`ToolCategory.WriteExecute`**, not `Network`. Every MCP tool reaches
an out-of-process surface, which is what `Network` says; a server this node launched unconfined on the host can
additionally write files and run commands as the engine's user, and the category an operator sees — and the node
approval policy tightens on — should say the stronger of the two. Sandboxed stdio tools and every HTTP tool keep
`Network`, because neither describes a host-write capability this node handed out.

Two properties are already in force for **every** MCP tool and are stated here so they are not re-implemented:

- **Never session-approvable.** `SessionApprovalEligibility.IsToolEligible` admits only `custom__` fixed tools and the
  two skill tools. No MCP tool of any tier can carry a remembered session approval.
- **Always audited before invocation on the one path that bypasses a human.** Every MCP tool is pre-wrapped in
  `ApprovalRequiredAIFunction` by the connection manager, and `SubAgentSpawnService` adapts exactly those functions
  through `McpAgenticToolAdapter`, which writes the ADR 0006 §6 strict metadata-only audit row **before** the inner call
  and blocks the call when the write fails. The human path records its resolved decision through
  `IToolApprovalAuditRecorder`. A second recorder for this tier would duplicate both.

## Migration of existing stdio registrations

Existing rows migrate to **`Sandboxed`**, not to `PrivilegedHost`.

`PrivilegedHost` would preserve today's behaviour silently and would leave every already-registered server permanently
outside the boundary this gap exists to close, with nothing in the UI to say so. `Sandboxed` is the secure default and
its failure mode is loud: a server that genuinely needs host access stops connecting and says why, in the status the
settings page already renders, with the tier named and `PrivilegedHost` named as the deliberate way to restore it. An
operator who re-grants host access has made a decision; an operator whose servers were quietly grandfathered has not.

The migration sets the column default and is a one-line `AddColumn` — no data conversion, so it is reversible.

## Environment at rest

Already done, and this document says so rather than repeating the work: `McpServerRegistration.EnvJson` is AEAD-encrypted
at rest by `NodeEncryptionSaveChangesInterceptor` with AAD column name `env`, and decrypted on materialization —
the same pattern the custom-tools `config_json` column uses. What Phase 2 adds is **masking on the way out**: the
`McpServerResponse.Env` map returns each configured key with a fixed placeholder value instead of the secret, and an
update that sends the placeholder back keeps the stored value. A secret that only ever travels inbound cannot be read
back out of the node by anything holding a session.
