# Inbound MCP tools

This table is the exact registered `NodeAgentMcpTools` plus `NodeAdminMcpTools` surface. Its first
two columns are parsed by `McpToolsReferenceDriftTests`; keep one exact, single-backticked tool name
and one exact scope in every data row. An `agentic` key sees both `delegate` and `agentic` rows; a
`delegate` key sees only the eight `delegate` rows.

| `tool` | scope | purpose | principal inputs / pattern |
|---|---|---|---|
| `list_agents` | delegate | List saved agents by id, name, and description. | — |
| `list_models` | delegate | List installed models with local identity, size, kind, and whether each is the current default. | — |
| `list_workspaces` | delegate | List authorized read-only workspaces as opaque ids and aliases. | — |
| `run_agent` | delegate | Run one bounded task synchronously. | `task`; exactly one of `agent`/`model`; optional model override, system prompt, workspace id. |
| `start_agent_run` | delegate | Accept a durable background run and return immediately. | Caller-generated UUID `request_id`; `task`; exactly one of `agent`/`model`; optional overrides/workspace. |
| `get_agent_run` | delegate | Poll a background run by `request_id`. | Durable across MCP disconnects and restarts. |
| `cancel_agent_run` | delegate | Durably request cancellation by `request_id`. | Lifecycle races return structured results. |
| `list_agent_runs` | delegate | List bounded, content-free lifecycle metadata. | Optional bounded `limit` and lifecycle `status`. |
| `get_status` | agentic | Get version, uptime, default model, and loaded llama.cpp process count. | — |
| `get_runtime_status` | agentic | Read installed/recommended runtime versions and update/offline state. | Cache-only; does not refresh the remote catalog. |
| `start_runtime_acquisition` | agentic | Start managed llama.cpp runtime acquisition. | Optional `variant`: `cpu`, `cuda`, or `vulkan`. |
| `get_runtime_acquisition` | agentic | Poll sanitized runtime acquisition progress. | — |
| `start_model_pull` | agentic | Start or rejoin a background GGUF pull. | `repo_id`; optional `file_name`, `quant`, `revision`. |
| `get_model_pull` | agentic | Poll a GGUF pull. | Canonical `model_name` returned by `start_model_pull`. |
| `cancel_model_pull` | agentic | Request cooperative GGUF pull cancellation. | Canonical `model_name`. |
| `delete_model` | agentic | Delete an installed model through coordinated deletion. | `model_name`. |
| `set_default_model` | agentic | Select an installed local model as node default. | `model_name`. |
| `get_node_settings` | agentic | Read the restricted core node-settings view. | Never returns secrets or unrestricted settings. |
| `update_node_settings` | agentic | Apply a partial update to the exact 18-field whitelist. | See **Settings whitelist** below. |
| `get_agent` | agentic | Get a saved agent by id or exact name. | `agent_id`. |
| `create_agent` | agentic | Validate and create a saved agent. | Required `name`, `instructions`; optional definition/provenance fields. |
| `update_agent` | agentic | Fully replace a saved agent by id or exact name. | `agent_id`, required `name`, `instructions`, plus the complete optional definition. |
| `delete_agent` | agentic | Delete a saved agent by id or exact name. | `agent_id`. |
| `list_workflow_runs` | agentic | List development workflow runs, one row per work item's latest run, as bounded lifecycle metadata. | Optional bounded `limit` and run `status`. Read-only. |
| `get_workflow_run` | agentic | Get one workflow run's status, node tallies, terminal reason, and per-node rows. | `run_id`. Read-only; no graph, artifacts, transcripts, or host paths. |

## Lifecycle contract

- `start_agent_run` requires a canonical hyphenated UUID `request_id` plus exactly one of `agent` or
  `model`. Repeating identical authority and inputs is idempotent; different inputs return
  `request_id_conflict`.
- `get_agent_run` reports `queued`, `running`, `succeeded`, `failed`, `cancelled`, `interrupted`,
  `result_expired`, `not_found`, or `invalid_request`.
- Results are capped at 24,000 characters and include `result_truncated` when clipped. Result
  payloads expire 24 hours after terminalization.
- Delegate execution is unchanged: ordinary saved agents and bare models are tool-less; the seeded
  Coder receives only `list_files`, `read_file`, and `search_text` for an authorized opaque workspace.
- Agentic root execution may use the saved agent's complete allowed-tool set. Approval-required tool
  invocations are audited before execution as metadata only; audit failure blocks the invocation.
  Spawned children retain ordinary curation and never inherit the root's agentic elevation.

## Settings whitelist

`update_node_settings` accepts only these 18 optional fields:

`default_model_name`, `enable_tools`, `tool_capable_models`, `hugging_face_default_quant`,
`llama_max_loaded_processes`, `llama_idle_time_to_live_seconds`, `keep_model_warm_enabled`,
`keep_model_warm_model_name`, `keep_model_warm_interval_seconds`,
`max_message_request_timeout_seconds`, `chat_cache_reuse`, `speculative_mode`,
`speculative_draft_model_name`, `speculative_draft_max_tokens`,
`speculative_draft_gpu_layers`, `kv_cache_type`, `reranker_model_name`, and
`auto_effort_fast_model_name`.

`auto_effort_fast_model_name` is refused unless it names an installed node-local llama.cpp model and
the node keeps at least two loaded-process slots: it is the model an `auto` reasoning-effort turn may
be moved onto, and that turn's context was admitted against a node-local model.

`CustomToolsEnabled` is deliberately absent: agentic settings updates cannot create an unattended
path to authoring host-command tools.

## Trust boundary

There is one singleton inbound-MCP key. Minting either scope rotates it atomically with no dual-valid
window. An `agentic` key is operator-equivalent only for the 25 tools above; it grants no Operator
role, JWT, browser REST access, routable listener, or general policy bypass. Do not log tool
arguments, prompts, message content, tokens, passwords, full keys, or host paths.
