# ADR 0020: The sandbox security profile is a stored, Operator-only node setting that promotes declared preferences to preconditions

- **Status:** Accepted (2026-10-08). Windows live evidence: tester rounds 6 and 8 of 2026-10-13 proved the `high` refusal
  and the first-run chooser's recommendation (see "Evidence (2026-10-13)").
- **Date:** 2026-10-08
- **Scope:** A node-wide choice between two sandbox security profiles, `low` and `high`. It decides whether a containment
  axis a workload PREFERS (egress denial, a stable filesystem boundary, resource ceilings) is merely requested, as
  today, or becomes a precondition that is refused fail-closed where the host cannot serve it. It changes no workload
  declaration, no floor, no selector ranking and no mechanism, and under `low` it changes no behaviour. (Amended
  2026-10-13: independent of the profile, a launch that has no filesystem boundary now gets the jail's own home
  directory as `HOME`, see `ProcessSandboxRuntimeProvider`; `low` otherwise still means today's behaviour. Amended
  2026-10-27: the host-toolchain Development declaration now prefers the filesystem boundary, so `high` refuses
  Development Mode on a host without the bubblewrap chain; the container declaration still asks for none. Decision 2's
  "Development Mode never asks" reads as the container form only. The warm restore still runs outside the chain under
  both profiles, because it needs egress. Operator decision "Warn and run" for `low`,
  `Plans/dev-mode-hardening-2026-10-27/PLAN.md`.)
- **Authority:** Operator decisions of 2026-10-08 (full proposal chosen over parking it or shipping switches only, with
  the pre-1.0 feature freeze lifted for this round; O1 `high` promotes only declared preferences; O2 the chooser sits
  after external access and before the interface mode; O3 the execution-previews switch moves into the new Sandbox &
  isolation section), recorded in `Plans/sandbox-security-profiles-2026-10-08/PLAN.md` (the plan directory is
  git-ignored; it is the record of the decisions, not a published document).
- **Amends:** the configuration-only ruling on tightening sandbox switches, recorded in the
  `SandboxOptions.RequireEgressDenial` documentation and in the residual-risk note of
  [ADR 0019](0019-execution-previews-and-the-appcontainer-boundary.md), for the profile only. It also amends that
  note's comparison of the two kinds of switch. [ADR 0007](0007-sandbox-execution-substrate-and-backend-selection.md)
  and ADR 0019 otherwise stand.

## Context

`RequireEgressDenial` is an Operator-configuration key. Its documentation gives the reason it is not a stored setting:
"a tightening switch something inside the node could clear is not one". That reasoning protected a switch that could
only tighten, and it left the operator with two keys, edited in files and applied at restart, that cover one axis.
Everything else a workload prefers degrades silently on a host that cannot serve it: Development Mode and work sessions
run with the host's network and no ceilings on Windows, and AgentHome runs without a filesystem boundary where none is
advertised. The Development isolation table reports what was served, but an operator who wants "refuse rather than
degrade" for every axis has no setting for it and no way to see, before choosing, which workloads it would refuse.

The first-run chain already has the shape of a decision made once: `/setup`, `/vault-setup`, `/external-access`,
`/ui-mode-setup`, each gated on a server-stamped discriminator, with a startup backfill so an upgraded node is never
asked a question it already answered. The external-access preset is the template.

## Decision

1. **One stored setting, three literals.** `StoredNodeSettings.SandboxSecurityProfile` is a string: `low`, `high` or
   `pending`. `null` means undecided and backfillable. `pending` means an administrator exists and the operator has not
   chosen yet; only the engine writes it, and the save validator rejects it. `NodeSettingsStore` folds an unknown value
   to `pending`, as it does for the external-access profile. There is no configuration seed: a fresh node is asked, an
   upgraded node is backfilled.
2. **Effective profile.** `high` only when the stored literal is `high`. Every other value, including `null` and
   `pending`, is `low`, so a node that has not chosen behaves exactly as before.
3. **One rule, shared.** `SandboxSecurityProfilePolicy` computes the preconditions from the workload's requirements, the
   host capabilities, the effective profile and the configuration switch:
   - **Egress denial is required** when the configuration key is set, or when the profile is `high` and the workload's
     network floor is `Unrestricted`.
   - **A filesystem boundary is required** when the profile is `high` and the declaration prefers a Stable boundary
     (`RequestsFilesystemIsolationWhereAdvertised`); it counts as served only where `RequestedIsolation` resolves to
     `Filesystem`, so the Preview AppContainer boundary does not count (ADR 0019, Decision 10).
   - **Resource ceilings are required** when the profile is `high` and the declaration asks for ceilings.
   - **Floors are untouched.** The `Filesystem` floor of `run_python` and Sandboxed MCP, and every other floor in
     `SandboxWorkloads`, are not relaxed by `low` and not restated by `high`.
   - **The warm-restore sandbox keeps its deliberate `Unrestricted` egress** under both profiles, because it is not
     agent-facing; ceilings apply to it as they do today.

   `high` promotes what a declaration asks for and nothing else. It never adds an axis the workload does not ask for, so
   Development Mode and work sessions, which never ask for a filesystem boundary, are not refused for one. Both
   enforcement and the capability summary call this one policy, so a row cannot say `Satisfied` while a create site
   refuses.
4. **Read per call.** The profile is read through `INodeRuntimeSettings` on every sandbox create, never cached, so a
   change applies to the next sandbox without a restart. The two configuration keys keep their meaning and bind at
   startup as before; they stay tighten-only ABOVE the profile (the key can add the egress requirement under `low`, and
   the profile can never remove it).
5. **Enforcement and refusal surfaces.** The create sites are AgentHome (shared by Coder), work sessions (their own create
   site in `McpWorkspaceExecutionSessionFactory`, on AgentHome's substrate), the Development workspace provider (both
   sandboxes), the Sandboxed MCP stdio transport and the compute gateway. An architecture test fails any create site
   outside the sandbox substrate that does not go through the policy. A refusal surfaces as:
   - `SandboxCapabilityNotSupportedException`, naming the profile and the remedy ("choose `low` on the Sandbox &
     isolation section"), for egress, filesystem and ceilings at the sandbox create sites;
   - for MCP, a connection failure reason `SandboxRefusedByProfile` with one fixed text that names the profile;
   - for `run_python`, the existing refusal code `NoResourceLimits`, with the profile named.
6. **The capability contract carries the answer.** The Development capability response reports the effective profile and
   the roles `high` would refuse on this host that `low` does not already refuse (`HighProfileRefusals`), so a role a
   configuration key, its own isolation floor or a withheld boundary already refuses is not blamed on `high`. Each isolation row reports whether filesystem
   isolation and resource limits are required, parallel to `NetworkIsolationRequired`, whether every required axis is
   served (`Satisfied`), and the network probe's reason when it is unavailable.
7. **First run.** The setup endpoint that stamps the external-access profile `pending` also stamps the sandbox profile
   `pending`. The route guard redirects to `/sandbox-profile-setup` when the profile is `pending`, after the
   external-access check and before the interface-mode check. The chooser offers `low` and `high`, preselects neither,
   has no skip control, and recommends `high` only when `HighProfileRefusals` is empty, otherwise `low`.
8. **Upgrade.** `SandboxSecurityProfileBackfillService` stamps `low` when the field is null, an administrator exists and
   the external-access profile is not `pending`. That is the discriminator `UiModeBackfillService` uses, so the three
   backfills are order-independent. A fresh install is never in that state: setup stamps both profiles `pending` in one
   write, so a restart between the two answers keeps the sandbox profile `pending` and the operator is still asked. The
   null-with-a-decided-external-access state exists only on a node whose setup ran on a build before this decision and
   that was upgraded between the two answers; it is not asked, runs under `low` (null reads as `low`) and is stamped `low`
   at its next boot.
9. **Operator-only.** The field is written only through the Operator settings endpoint. It is absent from the agentic
   patch, the agentic view and the node-admin MCP tool arguments, and a test asserts that absence. It is listed in the
   node-info report. Lowering it (`high` to `low`) asks for confirmation in the save flow.
10. **Previews stay a separate switch.** `high` does not enable execution previews and previews do not satisfy `high`.
    The previews switch moves into the same Node settings section so the two related Operator decisions sit together.

## Accepted residual risk

- **An Operator session can lower the profile.** A stored setting has an API path that a configuration file does not,
  and the server cannot tell a confirmed lowering from an unconfirmed one. The mitigations are that the field is
  Operator-only, never agent-writable (asserted by a test), confirmed in the UI on the high-to-low change, visible in
  the node-info report and on the settings page, and read per call so a lowering is observable at the next sandbox. This
  replaces the earlier reasoning that a tightening switch must be configuration-only, and it makes the comparison in ADR
  0019 less sharp: the previews switch widens and the profile tightens, and both are stored, Operator-only and
  confirmed on the widening change.
- **`high` refuses every executing workload on today's Windows.** The containment probe reports no separate egress
  mechanism and no resource limits there (ADR 0019, Decision 8), and every executing workload declares ceilings. The chooser recommends `low` on such a host. Operators who choose `high` there get a node that
  refuses to run sandboxed work, by design.
- **`high` refuses every executing workload on Linux without a systemd user scope.** All workloads declare ceilings and
  the ceilings are served by that mechanism.
- **Under the MXC Preview boundary, `high` refuses `run_python` and Sandboxed MCP for the missing ceilings** (the
  timeout-only execution accepted in ADR 0019, Q3). Turning previews on does not make `high` satisfiable there.
- **Raising the profile does not reach an already-connected Sandboxed MCP server**; it applies at that server's next
  connect or at the next node restart.

## Consequences

- Under `low` the profile changes no behaviour; the existing egress, ceiling and refusal tests pass unchanged. The one
  later change that touches `low` launches (the jail home as `HOME` where no filesystem boundary exists, 2026-10-13)
  is keyed on the missing boundary, not on the profile.
- The configuration-only ruling now has one documented exception, and the stale-belief table in the sandbox knowledge
  file says so.
- The rule is enumerated by an architecture test over `SandboxWorkloads`: the policy promotes exactly the declared
  preferences, so adding a declaration changes the rule's output only through the declaration.
- A `custom` literal is not introduced. The profile has two effective values because there is one rule; a per-axis
  choice would need a second knob.

## Still owed

- One AgentHome turn and one `run_python` call under `high` on a Linux host with a GPU and a systemd user scope (no refusal).

## Evidence (2026-10-13)

Windows tester round 6 (2026-10-13, Windows 11 25H2 build 26200.9457, MXC Preview boundary enabled): choosing `high` on the
Sandbox & isolation section saved without confirmation, the isolation panel listed Filesystem, Network and Resource limits
as required with only Filesystem and Network served, a Sandboxed MCP server start was refused with the
`SandboxRefusedByProfile` text naming the profile and the missing ceilings, already-connected servers kept running, and
lowering `high` to `low` asked for confirmation with the documented text. Round 8 (same host, fresh data directory): the
first-run chain asked the sandbox-profile question after external access and before the interface mode, offered both cards with
neither preselected and no skip, placed the "Recommended for this host" badge on `low`, listed AgentHome, Development mode and work
sessions as the workloads `high` would refuse (the same three the capability endpoint returned in `HighProfileRefusals`), and stored
`low` on Continue. Reports: the plan folder's `REPORT-6.md` and `REPORT-8.md`.
- A `custom` literal, if and when a second knob exists that the single rule cannot express.
