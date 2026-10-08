# Inbound MCP tools

This table is the exact registered `NodeAgentMcpTools` plus `NodeAdminMcpTools` surface. Its first
two columns are parsed by `McpToolsReferenceDriftTests`; keep one exact, single-backticked tool name
and one exact scope in every data row. An `agentic` key sees both `delegate` and `agentic` rows; a
`delegate` key sees only the eight `delegate` rows.

The third column states each tool's stability. A `stable` tool follows the compatibility rule in
[wiki 09](../../../docs/wiki/09-api-and-hubs.md): its name, arguments and result fields are additive-only within
the 1.x line, and a breaking change waits for the next major version. A `preview` tool may change without notice. Every tool below is `stable`.

| `tool` | scope | stability | purpose | principal inputs / pattern |
|---|---|---|---|---|
| `list_agents` | delegate | stable | List saved agents by id, name, and description. | — |
| `list_models` | delegate | stable | List installed models with local identity, size, kind, and whether each is the current default. | — |
| `list_workspaces` | delegate | stable | List authorized read-only workspaces as opaque ids and aliases. | — |
| `run_agent` | delegate | stable | Run one bounded task synchronously; reports progress every 15 s while generating. | `task` (≤ 32 KiB UTF-8); exactly one of `agent`/`model`; optional `modelOverride` (this spelling), `instructions`, `workspace_id`. |
| `start_agent_run` | delegate | stable | Accept a durable background run and return immediately. | Caller-generated UUID `request_id`; `task` (≤ 32 KiB UTF-8); exactly one of `agent`/`model`; optional `model_override` (this spelling), `instructions`, `workspace_id`. |
| `get_agent_run` | delegate | stable | Poll a background run by `request_id`. | Durable across MCP disconnects and restarts. |
| `cancel_agent_run` | delegate | stable | Durably request cancellation by `request_id`. | Lifecycle races return structured results. |
| `list_agent_runs` | delegate | stable | List bounded, content-free lifecycle metadata. | Optional bounded `limit` and lifecycle `status`. |
| `get_status` | agentic | stable | Get version, uptime, default model, and loaded llama.cpp process count. | — |
| `get_runtime_status` | agentic | stable | Read installed/recommended runtime versions, update/offline state, an active override (`overrideVariant`) and whether the install is a source build. | Cache-only; does not refresh the remote catalog. A non-null `overrideVariant` needs no acquisition. |
| `start_runtime_acquisition` | agentic | stable | Start managed llama.cpp runtime acquisition. | Optional `variant`: `cpu`, `cuda`, or `vulkan`. |
| `get_runtime_acquisition` | agentic | stable | Poll sanitized runtime acquisition progress. | — |
| `start_model_pull` | agentic | stable | Start or rejoin a background GGUF pull. | `repo_id`; optional `file_name`, `quant`, `revision`, `include_projector` (default `true`; `false` installs the weights only, which is what makes a vision-capable repo judge-eligible). |
| `get_model_pull` | agentic | stable | Poll a GGUF pull. | Canonical `model_name` returned by `start_model_pull`. |
| `cancel_model_pull` | agentic | stable | Request cooperative GGUF pull cancellation. | Canonical `model_name`. |
| `delete_model` | agentic | stable | Delete an installed model through coordinated deletion. | `model_name`. |
| `set_default_model` | agentic | stable | Select an installed local model as node default. | `model_name`. |
| `get_node_settings` | agentic | stable | Read the restricted core node-settings view. | Never returns secrets or unrestricted settings. |
| `update_node_settings` | agentic | stable | Apply a partial update to the exact 18-field whitelist. | See **Settings whitelist** below. |
| `get_agent` | agentic | stable | Get a saved agent by id or exact name. | `agent_id`. |
| `create_agent` | agentic | stable | Validate and create a saved agent. | Required `name`, `instructions`; optional definition/provenance fields. |
| `update_agent` | agentic | stable | Fully replace a saved agent by id or exact name. | `agent_id`, required `name`, `instructions`, plus the complete optional definition. |
| `delete_agent` | agentic | stable | Delete a saved agent by id or exact name. | `agent_id`. |
| `list_workflow_runs` | agentic | stable | List development workflow runs, one row per work item's latest run, as bounded lifecycle metadata. | Optional bounded `limit` and run `status`. Read-only. |
| `get_workflow_run` | agentic | stable | Get one workflow run's status, node tallies, terminal reason, and per-node rows. | `run_id`. Read-only; no graph, artifacts, transcripts, or host paths. |

## Errors and arguments

- Every failure is a tool result with `isError: true` whose JSON carries snake_case `failure_code` and
  `display_message`; agent and admin tools share this shape. A top-level `failure_code` always means the CALL
  failed. A successful `get_agent_run` of a run that itself failed, or whose result expired, is not an error: the
  run's own `failure_code` (`result_expired` for an expired payload) sits inside `run.metadata` and the top level
  stays clear; `start_agent_run` on an existing expired run answers the same way. Success results never carry one.
  `run_agent`'s answer is the model's own text and is never read as a typed failure, even when it is JSON.
- Arguments are checked against the advertised input schema before the tool runs. An unknown argument name, a
  wrong JSON type or a missing required argument returns `invalid_arguments` naming the parameter; nothing is
  silently ignored. The two run tools spell the override differently on purpose (`run_agent` takes
  `modelOverride`, `start_agent_run` takes `model_override`); the other spelling is rejected, not dropped.
- `task` is bounded to 32 KiB and `instructions` to 16 KiB of UTF-8 on both run tools (`task_too_large`); a task that still overflows the
  model's context window at run time is also `task_too_large`.
- Agent names are not unique. `get_agent`, `update_agent`, `delete_agent` and a run's `agent` resolve an exact
  name only when exactly one agent has it; otherwise `ambiguous_name` — use the id.
- An agentic run that ends without text is `no_answer`; one that stops on an unanswerable approval request is
  `approval_required`. Neither is reported as success.
- Every tool declares `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint` (always false).

## Lifecycle contract

- `start_agent_run` requires a canonical hyphenated UUID `request_id` plus exactly one of `agent` or
  `model`. Repeating identical authority and inputs is idempotent; different inputs return
  `request_id_conflict`.
- `get_agent_run` reports `queued`, `running`, `succeeded`, `failed`, `cancelled`, `interrupted`,
  `result_expired`, `not_found`, or `invalid_request`.
- Queued runs are dispatched oldest-first.
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
