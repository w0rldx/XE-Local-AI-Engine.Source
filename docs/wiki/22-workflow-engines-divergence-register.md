# Workflow Engines Divergence Register

> Reviewed: 2026-11-13 · Code-grounded.

**What this page covers.** A retired-engine note, plus one open addendum about Development Mode.

**Read this if you are** looking for Dev Workflows, or about to add a second tool-invocation stack. **Related pages:**
[Graph Workflows](21-graph-workflows.md), [ADR 0023](../adr/0023-dev-workflows-converge-into-graph-workflows.md).

## Dev Workflows is retired

This page used to compare the two workflow engines, Dev Workflows and Graph Workflows, which were copy-adapted and
shared no code. [ADR 0023](../adr/0023-dev-workflows-converge-into-graph-workflows.md) made Graph Workflows the one
workflow engine, and Dev Workflows is gone: its module, route family, hub, tables (migration `DropDevWorkflows`),
node setting, SPA feature and the Workflow kind of work session. The comparison went with it; the ADR records what was
ported and what was dropped on purpose.

Development work now runs as a Graph workflow:

- A `DevTask` node drives one Development Mode task (`implement`) and applies it (`apply`) through Development Mode's
  own apply gate ([Graph Workflows §4.10](21-graph-workflows.md#410-devtask)).
- An `apply` node is reached only through a `Pause` node's `Approve` decision (parser invariant `DEVTASK-1`), so an
  unattended run cannot apply.
- The `dev-intake` and `research-plan-approval` samples replace the seeded Dev Workflows templates
  ([Graph Workflows §6](21-graph-workflows.md#6-frontend)).
- The inbound MCP tools `list_workflow_runs` and `get_workflow_run` read Graph Workflow runs.

## 6. Addendum: adjacent divergence outside this register's pair (OPEN)

Development Mode is neither of the two engines this register compared. It is recorded here because it is the same shape of unexamined engine duplication the register existed to surface, and because the decision about it is still open.

| Gap | Cost guess | Note |
|---|---|---|
| `DevelopmentCoderModel.RunAsync` (`Services/Development/`) drives `IChatClient.GetStreamingResponseAsync` directly, with a hand-built `ChatOptions` tool set (its private `ToolGateway`, wrapped per tool in `AIFunctionFactory.Create`) and its own `ProviderCallBudget` scope — a second tool-invocation stack beside the shared `IInvocationRunner` that chat (`NodeChatStreamService`), Work Sessions (whose step loop drains that same chat stream service), Graph Workflows' Agent nodes (`GraphWorkflowInvocationExecutor`), scheduler `run-agent` (`RunSavedAgentHandler`), External Integrations (`IntegrationExecutionCoordinator`) and Benchmarks (`BenchmarkRunExecutor` / `BenchmarkJudgeExecutor` / `BenchmarkComparisonExecutor`) all route through. | L | **Undecided — recorded, not ruled.** The duplication is real, and a migration would be a re-architecture rather than a rename: the loop's gateway carries Dev Mode-specific pieces, including a cloud-only `submit_cloud_implementation` submission and the hash-locked apply-gate machinery, which ADR 0023 keeps in Development Mode and re-calls rather than ports. The 2026-09 feature audit's recommendation — a recommendation, not an owner ruling — is to **leave as is**. Treat this row as a fact about the code, not as sanctioned target architecture; revisit when a change would otherwise have to be made in both stacks. |
