# ADR 0021: Unattended actions are flagged custom tools inside the graph Tool node, audited before the side effect, with a per-tool private-host egress exception

- **Status:** Accepted — operator decisions of 2026-10-15 (plan approval); the design is not yet implemented.
- **Date:** 2026-10-15
- **Scope:** How a saved Graph Workflow may perform a real-world side effect with nobody present to approve it: which
  tools may do so, how a definition opts in, what is audited and when, how retries are prevented, how an operator-
  authored HTTP tool may reach one named host on the private network, and how a run records who or what started it.
  It changes nothing for chat, agents, inbound MCP, `web_fetch`, Dev Workflows or any existing graph.
- **Authority:** Operator decisions of 2026-10-15, recorded in `Plans/voice-to-action-assessment-2026-10-15/` (the
  plan directory is git-ignored; it is the record of the decisions, not a published document): the work is excluded
  from the 2026-10-06 feature freeze; LAN egress is a per-tool exact-host allow-list on an acknowledged custom tool;
  side effects widen the existing Tool node rather than adding a node kind; both custom-tool kinds are covered,
  because the target product is a desktop assistant that both calls a home-automation API and launches programs.
- **Amends:** [ADR 0006](0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) (the strict recorder is generalised),
  [ADR 0017](0017-web-access-tools-and-graph-allowlist.md) (a second exception to the `ReadLocal`-only rule),
  [ADR 0008](0008-external-integrations.md) (a `Graph` integration target) and
  [ADR 0013](0013-native-desktop-shell.md) (a command overlay window). Each carries a dated amendment.

## Context

Graph Tool nodes run unattended, so `ToolInvocationService.TryAdmit` admits only built-in `ToolCategory.ReadLocal`
tools that need no approval; `web_fetch` against an authored URL-prefix allowlist is the single exception (ADR 0017).
Retries make that envelope matter: a work node gets three attempts by default (`GraphWorkflowFailures`), and
`GraphWorkflowStartupReconciler` re-attempts a node that was in flight at a restart, so any side effect admitted
naively could fire twice.

The capability the product wants already exists in attended form. An `HttpFetch` custom tool sends GET, POST, PUT,
PATCH or DELETE with secret headers and a body template (`HttpFetchExecutor`); a `Command` custom tool runs a fixed
absolute, non-interpreter executable with an argv template, a cleared allow-listed environment and a tree-killing
timeout (`HostProcessExecutor`). Both are off unless the operator enables custom tools, both are wrapped in
`ApprovalRequiredAIFunction` (`CustomToolCatalog`), and every unattended path strips or refuses them. An
operator-authored tool is therefore the right adapter for "turn on the light" and "open my word processor"; what is
missing is a way to run one with nobody to ask.

ADR 0006 is the precedent for unattended side effects: authority is explicit, a strict scoped recorder writes a
metadata-only `ApprovalDecision` row before the call, and audit failure blocks the call. Reusing it keeps one
mechanism for "approved by policy, not by a person".

Home automation lives on the LAN. `CustomToolSsrfGuard` denies the private, loopback, link-local and reserved ranges on
two layers, `ValidateRequestUrl` for literal addresses and the shared, stateless pinned connect callback for resolved
ones, and no setting can admit a private target today.

Runs have no origin: `GraphWorkflowRun` stores only an optional chat binding, and every graph route sits behind the one
`Operator` policy. A schedule, an integration caller and a spoken command will all start runs, and the record must
say which.

## Decision

1. **The tool carries the flag.** Every custom tool, of either kind, gains a plaintext `AllowUnattended` flag on its
   stored record, beside `Enabled` and `Acknowledged`, readable without decrypting the configuration and not a
   version bump. Saving it requires the tool to be acknowledged and in `Fixed` mode, the verbatim operator-authored
   invocation the model cannot alter. Every existing guard on both kinds stays. The flag is the operator's statement
   that a graph may run this exact invocation with nobody present.
2. **The definition opts in.** A graph definition gains a plaintext `AllowsActions` flag, settable only through
   the Operator update route. Import never sets it and export never carries it, so a shared or downloaded graph arrives
   unable to act until the operator flags it on this node. `GraphWorkflowToolGate` rejects a Tool node naming a flagged
   tool, keyed by node, in a definition without the flag.
3. **A third admission branch, audited first.** `ToolInvocationService` admits a name that resolves through the
   custom-tool catalog to an enabled, acknowledged, `AllowUnattended` tool while custom tools are enabled. The
   invocable-tool listing marks such tools as actions so the editor can show them. On invocation the service first
   writes a metadata-only `ApprovalDecision` row through the ADR 0006 recorder, generalised to an unattended-approval
   audit recorder, with source `graph-action:<definitionId>`; only after that write succeeds does it call the kind's
   executor directly, without the approval wrap. Audit failure is a refusal outcome and the executor is never called.
   The human approval coordinator and its best-effort recorder are unchanged.
4. **One attempt, never re-fired.** The graph parser forces `MaxAttempts` to 1 on a Tool node naming an action
   tool, whatever the authored value. An action node row found `Interrupted` after a restart fails the run with a
   message naming the node; the reconciler does not re-attempt it. Idempotency beyond this is the target's own.
5. **A per-tool private-host exception.** An `HttpFetch` tool's encrypted configuration gains `privateHosts`: exact
   host names or canonical IP literals, each also present in `allowedHosts`, that this tool may reach inside the
   private ranges. Loopback, link-local, cloud metadata, multicast and unspecified addresses stay denied even when
   listed. Both SSRF layers enforce it: `ValidateRequestUrl` admits a listed literal, and the pinned connect callback
   reads the per-request exception from the request's options, so the fetch client stays single, the callback stays
   stateless, and with no option present the behaviour is exactly today's. A resolution that mixes a listed private
   address with any denied one is still refused. `web_fetch` gets no such list.
6. **Runs record their origin.** A run stores a `TriggerSource` (flags `Manual`, `Chat`, `Schedule`, `Integration`,
   `Intent`), and, where a principal started it, its `PrincipalId` and `KeyPrefix` (the latter audit metadata only, as
   in ADR 0008 ruling R4-6). A definition stores the `TriggerSources` it accepts, default `Manual | Chat`, settable only
   by an Operator and never by import. A start from an origin the definition does not accept is refused with the
   conflict envelope (ADR 0009).
7. **Unattended host commands are the highest-risk capability on the node, and are admitted anyway.** A flagged
   `Command` tool in a flagged definition launches a program on the operator's desktop with nobody watching. The
   mitigations are: the operator authors the executable and its argv; no shell or interpreter, a fixed working
   directory and a cleared environment; `Fixed` mode, so no model or utterance reaches the argv; acknowledgement at
   save; custom tools off by default; the definition flag import cannot set; the audit row before every launch; one
   attempt. The residual risk is the operator's own choice of executable. A launched GUI program appears only when the
   engine runs in the user's interactive session, as the desktop shell's owned engine does; an engine running as a
   service or attached from another session launches it invisibly or not at all.

## Consequences

- One mechanism covers both kinds, so "call Home Assistant" and "launch a program" share the flag, the gate, the
  audit and the retry rule; no Home Assistant code enters the product.
- Audit availability becomes a precondition of every unattended action, as it already is for agentic MCP.
- A request that reached its target before a crash is reported as failed and not repeated. An operator who needs a
  retry starts the run again.
- The SSRF guard gains its first exception path. Its tests must prove the listed host passes on both layers and that
  unlisted private, always-denied, and mixed resolutions are still refused, with absence of the option unchanged.
- A shared graph never brings authority with it: its action nodes are gate errors until the operator flags the
  definition on this node, and its accepted trigger sources reset to the default.
- Every graph change is also a Dev Workflows decision
  (`docs/wiki/22-workflow-engines-divergence-register.md`); Dev Workflows gets none of this.

## Alternatives rejected

- **A new `Action` node kind.** Rejected by the operator: the Tool node already has the invocation seam, the gate and
  the editor; a second kind would duplicate them and fork the run model.
- **A Home Assistant adapter in the product.** Rejected: an operator-authored `HttpFetch` tool already speaks the REST
  API; product code would pin one vendor and its versions.
- **MCP first.** Rejected: there is no per-server tool filter, every MCP tool is approval-wrapped, and a Home Assistant
  MCP server exposes far more than one action.
- **Widening `web_fetch`.** Rejected: it is GET-only and model-addressed by design (ADR 0017), and a private-network
  exception there would open the LAN to a model-chosen URL.
- **A confidence threshold on intent routing.** Rejected: the decision provider returns a constrained label and no
  confidence; an alias match, a router graph with an explicit `unknown` label and the user's Enter are the controls.
- **Routing every chat message automatically.** Rejected: a decision-model call per message costs seconds on a large
  model, and chat stays a conversation; commands enter through one intent endpoint and one page.

## Amendment 2026-10-15: the integration reading of ADR 0008, and the failed-action output

1. **Who authorises an action in an integration-started run.** Confirmed by the operator: such an action is authorised
   only by the operator's `AllowUnattended` flag on the tool and `AllowsActions` on the definition. The integration
   caller's key authorises starting the run. It is recorded as the run's origin (`TriggerSource`, `PrincipalId`,
   `KeyPrefix`, decision 6) and grants nothing beyond that.
2. **A failed action's output document carries the executor's text.** The node's error stays the structural reason
   `action-failed`; its output document holds the executor's text under output status `Failed`, bounded by
   `MaxOutputJsonBytes` (over the cap the envelope is empty). The reason never repeats the text.
