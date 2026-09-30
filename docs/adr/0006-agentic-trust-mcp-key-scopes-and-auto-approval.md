# ADR 0006: Agentic MCP keys capture bounded operator-equivalent execution authority

- **Status:** Accepted
- **Date:** 2026-08-22
- **Scope:** Inbound MCP saved-agent execution and its tool-approval audit only.

## Context

The inbound MCP server originally exposed a delegate credential. Delegate execution is deliberately restrictive:
bare models and ordinary saved agents are tool-less, while the forge-proof seeded Coder receives exactly three
read-only workspace tools. That principal has no browser approval channel and is not the node operator.

Agentic support adds a separately minted `agentic` key for unattended node setup and saved-agent execution. An
agentic run may use approval-required tools, but unattended execution cannot wait for a human response. The design
must therefore distinguish the trusted caller explicitly, preserve that authority across the durable queue, audit
every actual auto-approved invocation before side effects occur, and leave delegate and child-agent curation
unchanged.

## Decision

1. Authentication emits bounded `xe:mcp_scope` and `xe:mcp_key_prefix` claims. MCP tool methods receive the
   authenticated `ClaimsPrincipal` from the SDK and derive an explicit `McpInboundExecutionContext`. Ambient state,
   including `AsyncLocal`, is not authorization evidence.
2. The context is threaded through synchronous `run_agent`, durable `start_agent_run`, binding resolution,
   admission, dispatch, and execution. Scope and key prefix participate in request and binding fingerprints, so a
   request identifier cannot alias execution accepted under different authority.
3. Durable rows persist `is_agentic_auto_approve` and a bounded nullable `requesting_key_prefix` under a database
   consistency constraint. Legacy rows backfill as delegate. A run accepted before key rotation intentionally keeps
   its captured authority; rotation governs new admissions, not already accepted work.
4. Delegate execution retains its existing structural gate: ordinary agents stay tool-less and the seeded Coder
   keeps its exact read-only allow-list. Agentic root execution may resolve the saved agent's complete allowed-tool
   set, but missing or duplicate resolution fails closed.
5. Approval policy remains tighten-only. Only an `ApprovalRequiredAIFunction` in an agentic root offer may be adapted
   into a non-approval outer function. Child-agent curation remains unchanged and continues to remove
   approval-required tools.
6. On actual adapted-function invocation, a scoped strict recorder first writes a metadata-only
   `ApprovalDecision` with decision `approve` and source `mcp-agentic:<bounded-prefix>`. Audit failure blocks the
   tool. After a successful write the inner function is invoked exactly once. The existing best-effort human audit
   recorder and human approval coordinator are unchanged.
7. Structured invocation logs contain only tool name, category, bounded key prefix, request identity, decision,
   duration, and audit outcome. Arguments, prompts, message content, tokens, passwords, full keys, and host paths are
   never recorded.
8. Agentic is operator-equivalent only for the explicitly exposed inbound MCP surface. It grants no `Operator` role,
   JWT, browser API access, network-listener expansion, or general policy bypass.

## Consequences

- An agentic key is a high-value credential. Loopback-only reachability, explicit minting, rotation, bounded surface,
  and strict pre-invocation audit are the controls.
- Already admitted durable work is stable across disconnects, restarts, and key rotation. Operators must cancel such
  work explicitly if it should no longer execute.
- Audit availability becomes a prerequisite for approval-required agentic tool execution. This is intentional:
  unattended side effects fail closed when attribution cannot be persisted.
- Delegate integrations and spawned child agents do not inherit the agentic root's elevated tool offer.

## Alternatives rejected

- **Ambient authorization context:** rejected because it does not cross durable queues reliably and can be confused
  with execution-flow state.
- **Changing the human approval coordinator:** rejected because it would mix unattended machine authority with the
  existing human response path and its best-effort audit semantics.
- **Relaying approval back to the MCP client:** deferred; it adds a new correlation protocol and does not satisfy the
  unattended setup decision.
- **Giving the MCP identity the Operator role:** rejected; the browser/JWT administration boundary remains separate.

## Amendment 2026-09-30: the auto-approval wrapper must hide the approval marker

Decision item 6 was not in effect for any tool that needed it. MEAI's `FunctionInvokingChatClient` detects an
approval-required function with `GetService<ApprovalRequiredAIFunction>()`, and `DelegatingAIFunction` forwards
`GetService` to the function it wraps, so the adapter's `DelegatingAIFunction` over an `ApprovalRequiredAIFunction` was
still treated as approval-required. Each call became an approval request nothing answers, and the run ended as an empty
"succeeded" with no invocation and no audit row. `McpAgenticToolAdapter`'s wrapper now overrides `GetService` to return
null for `ApprovalRequiredAIFunction`, so the strict recorder runs and the inner function is invoked.
`SubAgentSpawnService` reports an empty answer (`no_answer`) or a leftover approval request (`approval_required`) as a
typed failure instead of success. Authority: `McpAgenticToolAdapterTests.FunctionInvocation_RunsAdaptedTool_InsteadOfRequestingApproval`.

## Amendment 2026-09-30: outbound MCP session scope

Outbound MCP registrations gain an opt-in `SessionScope`. `Shared`, the default and the behaviour every existing row
keeps, is one client session for every caller. `PerConversation` gives each chat or agent conversation its own
session, keyed by `AgentRunConversationContext.Current`, so a stateful server's per-session memory does not leak from
one conversation into the next. Inbound MCP runs, agentic ones included, never set a conversation id and always use the
shared session; this amendment grants them nothing. A per-conversation session idle for 15 minutes is disposed, and a
stdio server runs at most 4 per-conversation processes; the fifth conversation's call fails typed rather than falling
back to the shared session. Approval, trust tiers and the tool offer are unchanged.
