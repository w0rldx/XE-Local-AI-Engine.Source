# Open product decisions: the agent harness

- **Decision:** none filed. Each question below waits on an operator ruling.
- **Status authority:** this living page
- **Last verified against the tree:** 2026-11-26
- **Overall state:** six questions raised by the 2026-08-13 agent-harness architecture audit, none ruled. The code
  state under each is what ships today.

The repository cannot answer these: each is a product choice that decides how much complexity a harness change is
allowed to add. When one is ruled, record the ruling in an ADR or the owning wiki page and delete its section here.

## 1. Adaptive memory: on by default, or opt-in

**Question.** Should post-run memory extraction stay on for every Playbook-enabled agent, or become opt-in or
event-triggered (an explicit "remember")? The trade is recall lost against model calls saved.

**Code state.** `AgentDefinition.MemoryExtractionEnabled` (`Client.Persistence/Entities/AgentDefinition.cs`) defaults to
`true`; `AgentDefinition.PlaybookEnabled` defaults to `false` and gates it, so extraction runs only for an agent the
operator opted into Playbooks, and that agent can switch extraction off (retrieval-only).
`MemoryExtractionOptions.MaxConcurrentExtractions` defaults to 1.

## 2. Crash-resume promise for long workflows

**Question.** Is restart-durable continuation a product requirement for long workflows, or is safe terminalization
enough? Mid-step checkpointing is justified only by the former.

**Code state.** Graph Workflows runs resume after a host restart: `GraphWorkflowStartupReconciler` judges the node runs
left in flight before the dispatcher starts, and a restart loses at most the in-flight node's work
([wiki 21](../wiki/21-graph-workflows.md)). The preview workflows the audit named are gone. Open: whether an in-flight
node (an Agent turn, a `DevTask` step) needs its own checkpoint.

## 3. MCP scale target before tool discovery

**Question.** How many enabled MCP servers and tools should the Default Assistant support before a discovery step (a
tool search instead of the whole catalog) is required: tens, hundreds, or operator-curated only?

**Code state.** `LocalToolOfferProvider` appends every live MCP descriptor from `IMcpToolRegistry.GetDescriptors` to
the tool-capable offer; a model that leaves the node gets them only by grant or the `AllowCloudModelMcpTools` setting.
There is no tool-search or deferred-schema mechanism.

## 4. Retention of large tool results

**Question.** If large tool results become artifacts the model can page through, should they live for the invocation,
the conversation, or an operator-defined window? Security, disk quota and resume behavior differ.

**Code state.** No artifact store. `ToolResultBudget.Apply` (`AI.Agent/Tools/Implementation/ToolResultBudget.cs`) cuts
an oversized result to its head and tail with an explicit marker in the omitted middle; the cap is
`AgentToolPipelineOptions.MaxToolResultCharacters` (65,536), overridable through the
`StoredNodeSettings.ToolPipelineMaxToolResultChars` node setting. The cut text is not retained.

## 5. Hardware acceptance floor for harness work

**Question.** Which machines and models are the release floor for harness changes (CPU-only 16 GB, 8 GB GPU, 12/16 GB
GPU, 24 GB GPU)? Adaptive routing or concurrency defaults need at least two materially different local configurations.

**Code state.** [Wiki 13 "RC evidence requirements"](../wiki/13-testing-and-validation.md#rc-evidence-requirements)
requires the GPU smoke and the `--tier rc` model matrix on "a GPU box", with models pinned in
`scripts/model-matrix/models.json`. No hardware floor (RAM, VRAM, CPU-only) is defined.

## 6. External agent interoperability

**Question.** Is invoking or resuming external agents through MCP Tasks or A2A an intended near-term feature? If not,
monitor the protocols rather than add a layer.

**Code state.** Neither exists: no MCP Tasks client or server support and no A2A package or code. The node is an MCP
client (`IMcpToolRegistry`) and an inbound MCP server ([wiki 09](../wiki/09-api-and-hubs.md)).
