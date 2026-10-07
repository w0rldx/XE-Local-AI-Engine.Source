# Agent Mode reference: application services

> Reference page for [Agent Mode](../04-agent-mode.md) §2 · Reviewed: 2026-10-07 · Code-grounded.

It lists the service-area table, the agent-model refusal table, the resolved runtime projection, the full AgentHome write-back contract, the capacity and spawn inheritance rules, the turn-budget ladder and the coder and memory-worker details.

## 2. Application services (`Client.Application/Services/*`)

Continues [Agent Mode](../04-agent-mode.md#2-application-services-clientapplicationservices), which keeps the section introduction.

| Area | Key types | Responsibility |
|---|---|---|
| **Agents** | `AgentDefinitionService`, `AgentDefinitionResolver`, `AgentSkillService`, `ISkillImportService`, `AgentTemplateCatalog`/`Import`, `DefaultAgentSeeder`, `CoderAgentSeeder`, `OrchestrationResolver` | CRUD of agent definitions; per-turn resolution into a `ResolvedAgentRuntime` (prompt + gated tool offer + pinned model + skills + flags); two-phase import of third-party skills (§4.5). |
| **AgentHome** | `AgentHomeService`, `AgentHomeManifestService`, `AgentHomeWorkspaceService`, `AgentHomeGoalExecutor`, `AgentHomePatchService`, `NodePatchApplyService`, `IConversationSandboxStager`, `Tools/RunInAgentHomeToolHandler` | The write-back loop: sandboxed git workspace, bounded inner agent loop, patch export and apply, conversation-attachment staging. |
| **Analysis** | `PlaybookAnalysisService`, `DefaultPlaybookAnalysisAgent`, `IPlaybookAnalysisAgent` | Playbook analysis → **Suggested** staging (node-local model only). |
| **Eval** | (uses AI.Agent `IPlaybookEvalAgentRunner`) + `PlaybookActionService` gate logic | Golden-conversation eval gate (Suggested → Enabled). |
| **Insights** | `FeedbackInsightsService` / `IFeedbackInsightsService` | Read-only per-agent feedback aggregation (n≥3 threshold). |
| **Monitoring** | `PlaybookMonitorService` / `IPlaybookMonitorService` | Cohort monitoring of enabled playbook actions. |
| **Memory** | `MemoryExtractionService`, `MemoryExtractionDispatcher`, `DefaultMemoryExtractionAgent` | Adaptive agent memory: post-run node-local extraction → Suggested/Extracted, token-budgeted. |
| **Approval/audit** | `NodeToolApprovalPolicy`, `IToolApprovalAuditRecorder`, `ToolApprovalAuditRecorder` | Tighten-only category/name approval policy plus content-free approval-decision telemetry. |
| **Usage** | `AgentExecutionLogQueryService` (the endpoint's read door onto `IAgentExecutionLogStore`), `IUsageRateResolver`, `GetAgentUsageSummaryEndpoint` | Retained token-usage aggregation and operator-configured USD cost estimates; no message content. |
| **Capacity** | `CapacityService`, `ModelFootprintProvider`, `PendingFootprintLedger`, `SpawnSerializer`, `SpawnContext` | Capacity gate for spawning a model process (Allow / QueueSameModel / reject). |
| **Capacity/Sub-agent** | `SubAgentSpawnService` + `Tools/SpawnSubAgentToolHandler` | The `spawn_subagent` tool: capacity-gated, depth- and fan-out-capped child agents. |
| **Coder** | `CoderWorkspaceReader`, `Tools/{ListFiles,ReadFile,SearchText}ToolHandler`, `WorkspacePathGuard` | Read-only "coder mode": list/read/search files behind a path guard. |
| **Custom Tools** | `CustomToolService`, `CustomToolCatalog`, `HttpFetchExecutor`, `HostProcessExecutor` | Operator-authored HTTP/command tools; live SQLite catalog, author-time validation, secret masking, execution guards, forced approval. |

## 2.1 Per-message agent selection & attribution

Continues [Agent Mode](../04-agent-mode.md#21-per-message-agent-selection--attribution), which keeps the section introduction.

**Derived model requirements and the unattended refusal** (`AgentModelRequirements`). A saved agent requires tool
calling when its `AllowedToolNames` is non-empty or its `Kind` is `Orchestrator` (handoffs are tool calls); nothing is
declared on the definition, so the requirement cannot drift from the tool list. Image input needs no requirement: chat
withholds attachments and a graph node refuses them. When the effective model cannot call tools, each surface answers
with the same sentence from `AgentModelRequirements.ToolRefusal`, before capacity or any invocation:

| Surface | Outcome |
|---|---|
| Interactive chat (with or without a saved agent) | the turn runs, with one `ToolsWithheld` notice |
| Scheduler `run-agent` | `ScheduledJobExecutionException` ([Scheduler](../06-scheduler.md)) |
| Graph Agent node bound to an agent | node run `ValidationFailed` ([Graph Workflows](../21-graph-workflows.md)) |
| Integration invoke | `trigger-unavailable` (ADR 0008's closed vocabulary); EVERY integration run requires tools, because its output arrives through `emit_output` |
| Inbound MCP saved-agent run | `model_not_available`, only when the binding carries tools (an agentic caller or the seeded Coder); a non-agentic binding is tool-less by design |
| Work sessions | unchanged: `WorkSessionToolGate` already refuses |

Not checked: Dev Workflows' agent executor, and a graph Agent node with no bound agent (the default persona requires
nothing). The graph editor warns when a node's model override cannot call tools for a tool-using agent; the agent
editor's existing tool-capability and orchestration warnings cover its side. The "node default" choice is judged only
by the server.

### The resolved runtime projection

`ResolvedAgentRuntime` is what the resolver hands back, and its member order is load-bearing. The **first five**
fields map one-to-one onto a `LocalChatRuntimePackageRequest` input, so the existing builder and config-hash plumbing
are reused verbatim. **Everything after them is trailing, defaulted and deliberately outside the config hash** — the
builder reads only the leading five — so adding one changes neither an existing hash nor positional construction:

- `AgentDefinitionId` / `AgentName` — the provenance and display-name snapshot the stream service stamps as
  per-response attribution without a second fetch.
- `Skills` — the enabled, assigned skill set for MAF progressive disclosure. It is **not** folded into
  `ResolvedSystemPrompt`, because bodies load on demand, so the runtime-package builder folds it into the config hash
  separately and threads it to the invocation factory.
- `PlaybookEnabled` / `MemoryExtractionEnabled` — the gates the post-run memory-extraction seam reads without
  re-fetching the definition. Extraction fires only when **both** are true, while retrieval and injection stay gated
  on `PlaybookEnabled` alone, so a retrieval-only agent still injects existing memory but mines no new candidates.
- `Kind` — the definition's execution shape. The resolver has already loaded and decrypted the definition, so
  exposing it lets the chat-turn resolver decide whether to compile an orchestration without a second uncached read
  and AES-GCM decrypt on every send; the common non-orchestrator path skips the reload entirely.
- `DisableToolRelevanceFilter` — the per-agent opt-out from the send-time relevance filter, which narrows only the
  array handed to the provider, never the offer or the prompt.
- `PlaybookWithheld` — enabled playbook memory existed but was withheld from a cloud effective model under the
  `KnowledgeBase:AllowCloudModelAccess` gate; the send and regenerate paths turn it into ONE
  `TurnNoticeKind.PlaybookWithheld` notice. An orchestration carries the same fact per participant as
  `ResolvedOrchestration.PlaybookWithheldParticipantNames`, which the notice lists instead.

`DisableToolRelevanceFilter` and `PlaybookWithheld` are the members kept **off the wire** (`[JsonIgnore]`). This
record is serialized verbatim into the frozen v1 benchmark runtime snapshot, whose stored bytes are re-hashed to validate
`configurationHash`, so a new member emitting `false` would change the bytes of every already-frozen run and stop each
one replaying with "configuration hash is invalid" (`BenchmarkRuntimeSnapshotV1CompatibilityTests` guards this).
Omitting the filter flag is the honest shape as well: `BenchmarkRunExecutor.BuildPrimaryPackage` never threads it into the
replayed `RuntimePackage`, so a frozen run always generates under the node-level filter setting whatever the agent
asked for. Nothing else serializes the record — the agent-definition endpoint DTOs carry their own copy of the flag.

## 2.2 The AgentHome write-back loop

Continues [Agent Mode](../04-agent-mode.md#22-the-agenthome-write-back-loop), which keeps the section introduction.

**How `run_in_agent_home` is offered.** Registering the handler in DI reaches the *resolution* seam only, so the
descriptor is merged into the *offer* seam by `LocalToolOfferProvider` — on `run_python`'s terms, not the coder
tools':

- **Per-agent opt-in.** It lives in the profile pool (`GetOfferedToolsForProfile[Async]`), never in the whole
  chat offer, so a plain/mode-off turn and the seeded Default Assistant are never handed it. An operator must
  list `run_in_agent_home` in an agent definition's `AllowedToolNames` — the agent editor's tool picker can
  offer it because the known-tool catalog lists it ungated by model.
- **Local tool-capable models only.** The `AgentHome:ToolCapableModels` allow-list gate applies (read live per
  offer), and the tool is withheld outright from any model outside the trust boundary — a cloud-hosted, Codex-
  pinned or declared-cloud external id — because `run_commands` / `write_workspace` execute on the operator's
  own machine. That withholding is unconditional, not behind `AllowCloudModelAccess`.
- **Approval always.** The descriptor is `ToolCategory.WriteExecute` with `RequiresApproval: true`, matching the
  handler's hardcoded flag; the node policy can tighten it but never waive it. That is also what makes the
  unattended callers (sub-agent spawn, scheduler, graph workflows, delegate-scope inbound MCP) strip it, since
  each strips every approval-required tool rather than consulting a name list.
- **`AgentHome:Enabled` is enforced at execution, not at the offer**, like the compute and work-session
  kill-switches: the handler re-reads it and refuses before touching anything. The switch is the `AgentHomeEnabled`
  node setting (**Node Settings → General → Features**, read per call), which `AgentHome:Enabled` only seeds.
- **Sandbox backend.** A node whose `AgentHome:Sandbox:Provider` is unset resolves the no-op `fake` backend in
  non-Production, which executes nothing; the tool result says so explicitly rather than reporting a clean
  exit 0. Set `AgentHome:Sandbox:Provider=process` (restart-time) to execute for real.
- **Host patch apply is the operator's, and only the operator's.** A run *exports* `changes.patch` under its run
  directory automatically; landing it is a separate, human act. `INodePatchApplyService` is called from the
  `AgentHomePatch` endpoint pair (preview / apply, `NodeOperator`-gated — see
  [API & Hubs](../09-api-and-hubs.md)) and from the chat tool-result card's **Review and apply changes** dialog,
  which previews before it offers the button. The model cannot reach it: no `[McpServerTool]` and no
  `IClientLocalToolHandler` names the service, and `HostPatchApplyReachArchitectureTests` fails the build if one
  starts to. The apply also has to echo the hash the preview reported, so the diff the operator read is the diff
  that lands. Two things a run can produce are exported but never landed: a **symbolic link** (`mode 120000`,
  whose content is the link target, so applying it would point a real host link anywhere) and a **nested
  repository** (`mode 160000`), both refused by name in `NodePatchApplyService.ParseBlock` — see
  [Security & Privacy](../12-security-and-privacy.md). A refusal now also names the entry it is about
  (`<alias>/<relative>`, on `PatchApplyRejection.Path`), so the dialog can group the reasons per file. A
  C-quoted path — git's spelling for a name holding a quote, a backslash or a control byte — is decoded by
  `GitQuotedPath` before any guard runs and then applies like any other; a quoted literal that git itself could
  not decode is refused without a name. A path the workspace's `.gitignore`
  covers is not in the patch at all, because staging honours it (below). git runs with its repository discovery
  stopped at the folder's parent, so a folder inside another work tree applies from its own root, and a check that
  would not write every planned file (`git apply --numstat` missing one) is refused rather than reported applied.
  A run recorded as applied is never re-applied: an operator who reverts the files by hand must export again. That
  state is read from the same bounded head-and-tail window of the run's event log that the run list reads.
- **The preview also reads what the operator's own folders already hold.** For each selected folder the patch
  touches that is inside a git work tree, `PreviewAsync` runs one `git --no-optional-locks status
  --porcelain=v1 -z` through the same hardened `HostGitRunner`, scoped by pathspec to the patch's own targets,
  and reports the ones that are modified, staged or untracked (`NodePatchApplyPreview.DirtyTargets`). It is a
  **warning, never a gate**: `git apply --check` and the numstat coverage above stay the only things that decide `CanApply`, the warning is
  outside the hash binding, and it does not run at apply time. A folder that is no work tree reports nothing;
  a status call that fails or times out sets `DirtyCheckUnavailable` and leaves the preview otherwise intact.


**What a run does with the `goal`.** `AgentHomeService.RunAsync` hands it to `IAgentHomeGoalExecutor`
(`Services/AgentHome/Implementation/AgentHomeGoalExecutor.cs`), which runs a **bounded nested agent loop** on the
node: a MAF `ChatClientAgent` over the same `IChatClient` the outer turn runs on, built the way
`SubAgentSpawnService` builds a spawned child, invoked as an `AIFunction` inside a child `SpawnContext` scope.
Its tools work only on the sandbox's workspace copy, and `allowedActions` decides which of them exist at all:

| `allowedActions` value | What the loop is handed |
|---|---|
| `read_workspace` | `list_files` / `read_file` / `search_text`, served by the existing `CoderWorkspaceReader` against the same sandbox |
| `write_workspace` | `write_file` — one bounded UTF-8 file, written through the provider's own jail-guarded copy-in |
| `run_commands` | `run_command` — any executable, jailed, with the workspace copy pinned as its working directory |
| `export_patch` | the post-run patch export (unchanged) |

There is no fifth value. `propose_memory` was **removed** from the schema rather than left standing, because the
node collected the sandbox's proposals and then discarded them; the collector that read them has since been
deleted too, so nothing in the run path reads the sandbox's `memory/proposals/` directory. Persisting a proposal
into the adaptive-memory Suggested pipeline is a later slice that would add its own collector. (The shared
`MemoryProposalSecretScanner` is unrelated to that slice and stays: its live callers are `MemoryExtractionService`
and `DevelopmentArtifactSanitizer` — see [Security & Privacy](../12-security-and-privacy.md).)

**What bounds the loop.** Four budgets, separate from the sandbox's own per-command timeout and jail-disk
ceiling, all on `AgentHomeOptions`: `MaxRunSeconds` (the whole loop's wall clock — it matters most, because an
inner loop holds the node's single inference slot for as long as it runs), `MaxInnerToolCalls` (both are node settings read
once per run; the options only seed them),
`MaxWriteFileBytes` / `MaxTotalWriteBytes`, and `MaxCommandOutputBytes` for how much command output re-enters
the model's context. A budget that fires **cuts the run off and says so** in the tool result; it never discards
the partial work, which still exports as a patch. The shipped `MaxRunSeconds` default is a conservative starting
point, not a measured one — a live round on the target model class is what should set it.

**What confines the loop.** The inner tool list is built item by item from `allowedActions`, never read off the
tool offer, so the inner agent structurally cannot reach an MCP tool, a custom tool, `spawn_subagent`,
`ask_user`, a knowledge tool, an approval-gated tool of any kind, or a nested `run_in_agent_home`. It has no
human-in-the-loop route, which is why the operator's single approval of the outer call has to cover the whole
envelope up front. Every model-supplied path goes through `WorkspacePathGuard` and then the provider's
`ResolveJailPath` + `EnsureNoSymlinkComponentsUnderJail` pair, so an absolute path, a `..` climb, or a symlink a
command just planted is refused; writes anywhere under `.git` are refused outright, because that is where the
baseline the exported patch is diffed against lives, and a `write_file` outside every copied folder — a file
beside them at the workspace root — is refused naming the folders it may use, because the export diffs only
those folders and such a file would be work the operator never sees. File contents and command output are fenced with
`UntrustedContentFraming` before they re-enter the model, and the sandbox's own root is stripped from captured
output so no host path reaches it.

**Export stages before it diffs.** `git diff HEAD` does not see an untracked path, so an export that diffed the
working tree left every file the run *created* out of `changes.patch` and `changed-files.json` while the run
reported it as written — silent data loss the moment the operator can apply a patch. Export therefore runs
`add -A` first and diffs `--cached` against the baseline. Deliberately **not** `--force`: the workspace copy's
baseline `add -A` honours `.gitignore`, so forcing would report every ignored-but-copied file as one the run
added, and the two sides of the comparison have to apply the same rule. A model that hides its own creation
behind a `.gitignore` shrinks what the operator is offered but cannot get an unreviewed file onto the host; the
run's `run_completed` event carries `changed_files` beside `files_written` so the two numbers can be compared.

**And it diffs the copied folders, not the repository.** All three export commands — the `add -A`, the patch
diff and the `--name-status` diff — carry the copied folders' aliases as `:(literal)` pathspecs instead of `.`.
A whole-repository pathspec let a file at the workspace root into `changes.patch` and the `+`/`-` totals while
the alias split dropped it from `changed-files.json` and `ChangedFileCount`, so the model was shown a file count
that did not match the line count, and the host apply refused the *whole* patch — valid hunks included — over
that one block with no alias segment. Scoping makes the patch text, the name-status stream and both counts
describe one set by construction. The mapping keeps its skip for an entry with no alias, which now only a stream
the node's own git did not produce can trigger.

**The patch export's own git is hardened, and logged.** Export runs `git diff` over the workspace the model just
had `write_file` and `run_command` access to, **after** its turn ended and outside the run's budgets — and git
executes programs named by configuration (`diff.<name>.textconv`, `diff.external`, `filter.<driver>.clean` on any
content conversion, `core.fsmonitor` on an index refresh). Measured on git 2.53.0 against the exact export
argument vector: a repository-local `textconv` and a repository-local `clean` filter both executed, and so did a
driver defined in the **global** config (the sandbox forwards `HOME`). `AgentHomeGit`'s `-c` pins cannot close
that class — driver names are arbitrary and git has no flag that disables attribute processing. Neither do
`--no-textconv` and `--no-ext-diff`: the clean filter still ran under both, so they are belt and braces here, not the
control.
`AgentHomeGitHardening` closes it structurally instead, on the principle that a driver has to be **defined in
configuration** to run, so an in-tree `.gitattributes` naming an undefined one is a no-op:

- the repository's own `.git/config` is rewritten to a node-owned allow-list immediately before every node git
  invocation, reusing Development Mode's `DevelopmentWorkspaceGitConfig` rather than growing a second such rewrite;
- the global and system files are removed from git's search entirely by `GIT_CONFIG_GLOBAL` (pointed at the null
  **device**, which nothing can turn into a config file), `GIT_CONFIG_NOSYSTEM` and `GIT_ATTR_NOSYSTEM`;
- a `.git` that is not a real directory — a gitfile pointing at a model-owned git directory defeats a rewrite
  outright — **fails the export closed** rather than exporting a patch the node cannot vouch for;
- and every git invocation the node makes itself is written to the run's `commands.jsonl` with `actor: node`,
  beside the model's own `actor: model` entries, so an audit sees the whole sequence rather than half of it. That
  includes the export's own staging `add`, and the workspace copy's **baseline** git (`init` / `add -A` /
  `commit`), which runs during *prepare* —
  before a run id or a log exists — and is carried out of preparation on the prepare result and flushed the
  moment the log opens. Each record keeps the timestamp and duration from when its command really ran, and
  carries argv, exit code and duration only: never a byte of what the command printed.

**The `-c` pins and the byte-stability pair.** `AgentHomeGit.Arguments` prefixes every AgentHome git invocation with
the hardened `-c` set, because a command-line `-c` outranks every config file, `include.path` chains included. The
exec-bearing key that is actually live there is `core.fsmonitor`: a value planted in a repository-local `.git/config`
runs as a shell command on the first index refresh — `status`, `reset`, `add` and `diff` all trigger it — and that is
reachable from the **host**, not only the sandbox, because `DevelopmentPatchEvidenceService` runs `reset` and `add -A`
with its working directory set to the workspace and the workspace `.git/config` is writable from inside the container.
`core.sshCommand`, `core.pager` and `core.editor` are not reachable from today's command set (no network operation, and
stdout is always redirected so no pager spawns) and are pinned anyway, so adding a fetch or a paging command later
cannot quietly re-open the hole. `core.autocrlf=false` and `core.filemode=false` are *not* in that shared set:
`AgentHomeGit.WorkspaceArguments` adds them on the command line for the baseline and the export only. On the command
line because the pre-invocation config rewrite would drop a stored value before the diff; not in the shared set because
Development Mode, Dev Workflows, knowledge-repository import and host patch apply run `Arguments` against the
*operator's own* checkouts, where forcing `core.autocrlf` would change how a patch renders or applies on a repository
that legitimately stores CRLF — Development Mode derives that policy from the repository's own index
(`DevelopmentWorkspaceWhitespacePolicy`) precisely so that it is not forced.

**Which host git carries the hardening environment, and which deliberately does not.** `HostGitRunner` and
`DevelopmentPatchEvidenceService` (its exact-bytes runner, which clears the environment and points `HOME` at the
attempt's runtime directory) both apply every entry of `AgentHomeGitHardening.Environment` on top of the `-c` pins and
the `.git/config` rewrite: the evidence service's `add -A` runs over an agent-written tree, so a driver defined in the
system file or in that `HOME`'s `.gitconfig` would otherwise run when an in-tree `.gitattributes` names it.
`TrustedDevelopmentHostApplyPort` deliberately **inherits** the operator's environment and global and system
configuration: it runs against the operator's own registered repository with a human-approved patch hash, and that
configuration is what makes the apply correct there — commit identity, credential helpers, `safe.directory` for
other-owner or mounted repositories, and `core.autocrlf` on Windows, where changing it would change the applied bytes
against the approved `PatchHash`. Its closure is the `-c` exec-key pins alone, and it never rewrites that repository's
`.git/config` (`TrustedDevelopmentHostApplyPortHardeningTests`). The residual is stated: an approved patch whose
`.gitattributes` names a driver the operator's own configuration already defines.

Two residuals are stated rather than claimed away. The rewrite happens immediately before the invocation, so a
process a `run_command` deliberately double-forked could in principle rewrite the file again in between — the
same window Development Mode accepts, and not closable on a soft jail with no filesystem boundary. And a model
that rewrote the git objects or HEAD could make the exported patch misrepresent the tree; that bounds what a
patch proves, and the operator reviewing it is the control.

**The result's first line is a contract.** Line one is a node-authored header, start-anchored, in exactly this
shape:

```
[agent-home run=<run-id> outcome=<Token> patch=<exported|none>]
```

Everything after it is prose, and that prose embeds the **model-authored** `run_command` executable *before* the
run's genuine `runs/<id>/patches/changes.patch` string. A reader that recovered the run id by scanning the prose
could therefore be handed `runs/<other-run>/patches/changes.patch` by a model that simply named its executable
that, and would offer the operator a different run's patch. **Position is the control**: every field in the
header is node-derived, the line is emitted before any model-authored byte, and nothing model-authored can reach
index 0 (the one model-authored string the summary renders is also flattened to a single line, so it cannot even
produce a second line that looks like a header). `patch=exported` means `changes.patch` was really written — a
patch over `MaxPatchBytes` reports `none` although `changed-files.json` exists. A **rejection** (busy, unknown
folder id, disallowed runtime profile) carries no header at all: no run was created, so there is nothing to
offer, and a missing header means "no run" rather than "this turn's run". Parse it start-anchored or not at all;
do not reorder the fields, add one, or widen a value.

**What the outer model is told.** The tool returns a plain string (every `IClientLocalToolHandler` does), built
only from facts the **node** derived: the header above, the run id and run-relative output paths, a stop-reason
sentence, the per-folder copy counts, the loop's tool-call /
refusal / written-file counts and elapsed seconds, an aggregate of the commands run with their exit codes, the
patch's changed-file count, its added/removed line totals and byte size, the `fake`-backend honesty notice, and a fixed closing sentence saying
the run has ended and need not be repeated (a live round watched a 27B model re-invoke the tool two and three
times in one turn because the summary was too thin to trust). **Command output and workspace file bytes never
appear there**, and neither do the paths `write_file` wrote: the outer model still holds the node's other tools,
so workspace-authored text arriving as "your tool result" is steering text with a delivery mechanism. The detail
lives on disk, under `runs/<run-id>/`. `AgentHomeToolResultContainmentTests` plants one marker through all three
routes at once — a command's stdout, a written file's content, and the path the model chose for a second file — and
grades the returned string on its absence.

**Writes the patch does not carry.** The loop records every path `write_file` wrote. At export time the node
compares that list against the paths the diff names and classifies whatever is missing: hidden by a `.gitignore`,
deleted again before the export ran, byte-identical to the baseline so there is nothing for a patch to carry, or
**unexplained** — the node saying plainly that it cannot account for a file the run wrote, which is the shape a
silently incomplete patch takes. The full breakdown, with the paths, is written to the run's own `events.jsonl`
and, as the argument of the classifying git call, into its `commands.jsonl` — operator-only run logs, never the
tool result, because those paths are workspace-authored; the tool result gets the per-reason counts from a
fixed template and never a path, the apply preview gets nothing at all (it previews the patch, not the run), and
an unexplained path additionally raises a host warning naming the run. A gap is never a failure: it does not
block the export, change what the operator may apply, or alter the header. Two limits are worth stating rather
than discovering. What a `run_command` changed on its own is outside this ledger entirely — the node sees what
its own write tool was asked to do, not what a command did — so a command that created a file the patch missed
is not caught here. And an ignored file is *reported*, not exported: staging honours `.gitignore` exactly as the
baseline did, so the honest answer for one is "it is not in the patch, and here is why". The classification asks
git two further questions, and only when something is missing to ask about, so a run whose writes all reached
the patch pays nothing for it.

**It only runs from an approved chat turn.** The loop reads the outer model id off the ambient `SpawnContext`
root that `InvocationRunner` seeds, and refuses when there is none or when that model is outside the node's
trust boundary. The unattended entry points seed a root without a model id, so they cannot reach it even if the
approval gate were somehow bypassed.

**`run_command` ships only behind a real filesystem boundary.** The process jail confines a command child's
**network**, **environment** and **resource ceilings** and pins its working directory — but under
`SandboxIsolationMode.None` it is **not a filesystem boundary**: the command reads and writes any path the
engine's user can, including the operator's original registered folder. So AgentHome asks for
`SandboxIsolationMode.Filesystem` wherever the provider advertises it (a request the registry refuses
fail-closed if it cannot be met, which is why it is gated on the advertised capability and can never sink a run),
and the goal loop then reads the boundary it actually **got** off `SandboxHandle.Isolation`:

- **boundary delivered** → `run_command` joins the inner tool list. The child sees a read-only `/usr`, the
  workspace at `/work`, a `HOME` inside the jail, and no network; a path outside is not in its mount namespace at
  all, so it can be neither read nor written.
- **no boundary** (a Linux host without user namespaces or bubblewrap, and **every Windows host today**) →
  `run_command` is **withheld even though `run_commands` was allowed**, the inner prompt tells the model so, and
  the tool result says *"commands were not available: this node cannot isolate the sandbox file system"*. The
  read tools and `write_file` stay, because they are confined by the node's own `WorkspacePathGuard` and the
  provider's no-follow file surface rather than by the jail.

`ProcessSandboxFilesystemReachTests` measures both sides, and `AgentHomeGoalExecutorTests` pins the exact inner
tool list with and without the boundary. See [Security & privacy §7](../12-security-and-privacy.md).

**One sandbox posture, asked for once.** The isolation request is made for *every* run, not only when `run_commands`
was granted, for three reasons. It cannot sink a run, because the request is gated on the capability the provider
advertises and that advertisement is mechanical — the same probe the launch path reads. The sandbox is owner-node
scoped and **reused across runs** through `CreateOrAttach`, so a per-run request would be a lie on the attach path:
the second run would silently inherit the first run's boundary while believing it had asked for its own. And a
read/write-only run is no worse off for having the boundary, while the chat attachment re-stage creates the same
sandbox, so one posture keeps the two entry points from disagreeing. **Egress** is requested the same way and
default-denied wherever the provider can enforce it: everything AgentHome and Coder run inside the sandbox is local
(`dotnet --version`, the baseline git commands under `/dev/null` hooks, Coder's `find`/`grep`), so denial costs no
supported capability and removes the child's reach to the node's own loopback API, the LAN and the cloud-metadata
endpoint. Capability-gated rather than unconditional for the same reason — asking for confinement a host cannot
provide would stop AgentHome running rather than harden it — with the degradation visible in the sandbox containment
log rather than silent. A node that wants the refusal instead sets `AgentHome:Sandbox:RequireEgressDenial`, which
`SandboxEgressPolicy` turns into a fail-closed refusal naming that key.

**What `Prepare` does before the loop starts.** `AgentHomeService` builds the attach key, recovers the worker-local
layout, attaches or creates the sandbox, resolves and copies the selected folders, and creates the git baseline;
`Run` then hands the `goal` to the executor and feeds the gated patch export and run-scoped logging. The
service-level tests exercise orchestration, busy/cancel/owner hardening and the gated patch export against the
deterministic provider, while the executor's own tests drive the real inner tools with a scripted chat client — the
configured runtime provider is what supplies real command execution and git behaviour.

**End to end, with the operator holding the last step.** An exported patch is previewed and landed through the
`AgentHomePatch` endpoint pair and the chat card's apply dialog (above). What a run still has no surface for is
listing its own history: runs accumulate on disk with no retention sweep and no run list, so an operator reaches
a patch through the tool result that produced it.

**Conversation-attachment staging** (`IConversationSandboxStager`,
`Services/AgentHome/IConversationSandboxStager.cs`). `AgentHomeService` also implements this narrow public
seam (`AgentHomeService.PrepareConversationAttachmentsAsync`) so the public
`NodeChatStreamService` can stage a chat conversation's uploaded attachments into the per-turn sandbox
without an inconsistent-accessibility error. When an agent-mode turn offers file tools, the stream
service re-stages the owner-node sandbox to hold **only** that conversation's extracted attachments under
the workspace `attachments/` alias (the sandbox is recreated first, so there is no cross-conversation
residue), then hands the model the staged workspace-relative paths so its `list_files` / `read_file` /
`search_text` tools read them directly. Because the same shared singleton implements both interfaces, the
re-stage shares the run-level single-flight guard with `run_in_agent_home`. It is a no-op (empty list)
when Agent Mode is disabled or the conversation has no extracted files. The chat-side wiring — and the
contrasting plain-chat path that *inlines* extracted text instead of staging — is in [Chat](../05-chat.md).

### Resolving a selected folder by id or alias

`SelectedFolderResolver.ResolveAsync` accepts either form the node hands out: the opaque GUID, or the human-facing
**alias** that Node Settings displays and that `run_in_agent_home`'s schema advertises. The GUID is tried first,
because an alias of GUID shape is registrable — `NormalizeAlias` leaves lowercase hex and hyphens untouched and
`AliasShapeRegex` accepts the result — so an opaque id must never be shadowed by an alias that merely looks like one.
Falling through to the alias lookup only when the id lookup misses is what keeps such an alias reachable rather than
permanently masked.

The alias is matched **exactly, never normalized**. Stored aliases are already canonical, because registration
normalizes before persisting, so an exact match on a canonical input finds precisely what exists; refusing to normalize
here keeps this seam from quietly resolving `My Scratch` to `my-scratch` for a caller that passes unvalidated text.
Both lookups go through the same store, which returns `null` for a revoked folder, so alias resolution reaches exactly
the records the GUID path reaches and no others.

## 2.3 Capacity gate & sub-agent spawn

The section introduction is in [Agent Mode](../04-agent-mode.md#23-capacity-gate--sub-agent-spawn).

### What the capacity gate admits without probing

`CapacityService.DecideAsync` short-circuits twice before it reads any byte budget.

A **cloud-routed model** is admitted unprobed: its sends cost this node no bytes and no process. The check is keyed
on the request's `modelName` rather than on the node-default selection, because `RuntimeChatClient` re-selects the
provider per send from the same per-request model id — so an active Codex session must not exempt a spawn that
explicitly names a local model, and a local model must still be admitted on its own footprint while Codex is
signed in.

An **operator-registered external OpenAI-compatible model** is admitted unprobed for the same reason: it runs
entirely on someone else's hardware, the node starts no process and loads no weights, and the footprint provider
has no GGUF to size — so *not* admitting it would be actively wrong, rejecting every such send as "footprint could
not be determined". Two conditions are checked on purpose. The provider-map row written by the save path is the
normal route; the model id's `ext:` scheme is the backstop for the window where that row is missing — a crash
between the encrypted-store commit and the map sync, or a row the reconciliation pass has not repaired yet — in
which the model would otherwise default-route to `llamacpp` and be rejected on a footprint it can never have.
Neither branch grants anything: capacity admission is about local resources only, and an external model's trust and
egress decision is made elsewhere, from its operator-declared locality.

Every **reject** names the requested model and the loaded `(model, role)` set in its reason ("Insufficient capacity for '…' (Chat): not enough free memory for another model. Loaded now: '…' (Chat), '…' (Embedding). Eject one of them or pick a loaded model.") and logs one Warning (`Capacity rejected {Model} ({Role}): …`), so a refused graph-workflow node or spawn is visible in the node log; the gate never evicts a resident model.

The rest of the decision runs under `IPendingFootprintLedger.EnterDecisionAsync`. The device audit is warmed
**before** that gate, because its bounded, cached `--list-devices` probe would otherwise serialize every capacity
decision behind a one-time probe. The hardware profile is then force-refreshed **under** the gate: an admission
decision runs per model-load — rare, and already serialized — so it reads a live VRAM/RAM snapshot rather than the
profiler's boot-time cache, and the gate bounds it to one probe in flight. `GetEffectiveProfileAsync` degrades that
profile to CPU-mode when the audit reports a silent CPU fallback (see
[03-local-runtime-and-providers.md](../03-local-runtime-and-providers.md) §2.5), so a GPU box whose runtime enumerates
no devices sizes against system RAM instead of pretending VRAM exists.

### What a profile-bound child inherits

A profile-bound child consumes the SAME complete `ResolvedAgentRuntime` a direct agent send does — resolved once,
from the definition snapshot already read rather than by id, so a concurrent edit cannot assemble one child out of
two versions. It therefore inherits the resolved system prompt (scaffold + persona + injected playbook memory),
reasoning effort (gated on the child model's own thinking capability, mirroring `ParticipantReasoningOptions`),
skills (MAF progressive disclosure), AND its curated tools — not just the tool set. This is structurally the
orchestration-participant path: an agent-as-tool never receives the outer runner's per-run `RunOptions`, so
reasoning and skills must be baked into the agent at construction.

A profile-bound child's curated tools are `offer ∩ AllowedToolNames`, minus `spawn_subagent` AND any approval-gated
tool — a child has no HITL route to answer an approval request (§4.4). A model-id-only child (no profile, no
`AllowedToolNames`) stays as it was: raw request instructions, tool-less, no reasoning and no skills. Post-run
adaptive-memory **extraction** stays disabled for a child, an intentional restriction.

**The child pins its own external binding.** `SubAgentSpawnService.RunSubAgentAsync` resolves the child's pin through
`ExternalProviderInvocationPin` and opens `ExternalProviderBindingPinScope` for it, exactly as `InvocationRunner` does
for the parent turn. The parent's pin is keyed by the *parent* model, so without this an external child's sends find no
pin and fall through to the transport's weaker unpinned check — **while the child is running with a tool set that was
authorized against the declaration read here**. That is the reason the mismatch matters: a turn decides once, before
its first send, which tools the model may be offered from the connection's declared locality, and the pin is what makes
every later send in the tool loop verifiable against that same declaration.

The scope is opened **synchronously, in this frame**, and not inside the async helper: an `AsyncLocal<T>` written
inside an `async` method is invisible to its caller, so a pin seeded there would not survive the helper's return. The
child run happens inside this frame's flow, so the scope reaches it; pins stack, so the parent's pin lives on
underneath and is restored on dispose.

## 2.4 Usage and estimated cost

Continues [Agent Mode](../04-agent-mode.md#24-usage-and-estimated-cost), which keeps the section introduction.

**The terminal telemetry rides all three terminal paths.** `InvocationRunner` reports the turn's tool-schema token
estimate, the model-readiness duration and the turn's **summed** usage onto the invocation state immediately before
it reports the turn completed, cancelled or failed, so the terminalize write persists them with the envelope row.
Failure and cancellation carry them too, because the numbers are most interesting on a turn that ran out of context;
the readiness duration is `null` on any turn with no local warm (Ollama, a remote provider), which is what that
column means; and the summed usage is the turn's **cost**, as opposed to the last round's counts that
`ReportInvocationCompletedAsync` puts on the message row. The report runs *before* each terminal report and swallows
its own faults, so a throw can neither turn a finished turn into a failed one nor replace a real failure
classification. `CaptureEfficiencySnapshot` is a pure counter read, so calling it on every path is free. Counts
only: no tool name, prompt or tool result reaches this seam.

## 2.5 The composite turn budget

Continues [Agent Mode](../04-agent-mode.md#25-the-composite-turn-budget), which keeps the section introduction.

The three timeouts are a ladder; a stalled or slow turn trips them in this order, tightest first:

| Bound | Source | Enforced by | What it bounds |
|---|---|---|---|
| `StreamIdleTimeout` | package `TimeoutSettings.StreamIdleTimeoutSeconds` | `StreamIdleWatchdog` | No chunk arrives between two yielded items of ONE streamed segment, while no requested or approved tool call awaits its result (server-side tool execution carries its own bound) and the provider round has already produced its first output (the prefill wait before it is bounded by `InvocationTimeout`) |
| `ToolResultTimeout` | package `TimeoutSettings.ToolCallTimeoutSeconds`, else the node-global pending-tool-call age | `ApiToolCallBridge` | The wait for a tool call's RESULT |
| `InvocationTimeout` | package `TimeoutSettings.InvocationTimeoutSeconds` | `InvocationLifecycleTracker` | The whole turn's wall clock, every segment and approval round-trip end to end |

Two splits in that ladder are deliberate and stated rather than quietly unified. The orchestration path
enforces an analogous **per-quiescence** idle bound in `OrchestrationRunSession`, but that timer comes from
the node-global `OrchestrationAgentOptions.IdleTimeoutSeconds`, not from the package field above. And the
human-**approval** wait always uses the node-global pending-tool-call age, never the shorter per-tool
`ToolResultTimeout` — a person is not a tool call.

Context budgeting (`ContextCapacityTokens` / `ReservedOutputTokens`) is orthogonal to all three: it bounds
what history is *sent* to the provider, not how long the provider is given to answer. `ReservedOutputTokens`
is the larger of `ReservedOutputTokenFloor` and the package's own `MaxOutputTokens`, unless the package carries
`ReservedOutputTokensOverride` — set only by the benchmark primary executor, to the project's output cap — which
is taken exactly, and is also the floor of that invocation's provider-round budget (`ProviderCallBudget` scope).
The outer budget runs at two points, the initial assembly (conversation only) and each tool-loop round (the agent
factory's seed: the system prompt as its leading System message, then the conversation). Tool definitions are
always fixed overhead; the system prompt is fixed overhead only when the history does not already carry it, so both
stages size the same request and a prompt is never paid for twice (`ConversationContextBudgeter.Budget`).
`RetryEnabled` / `MaxRetries` / `CircuitBreakerEnabled` govern only the pre-first-token send of the **first**
segment (`ProviderStreamResilience`). `MaxToolIterationsPerRequest` and
`MaxConsecutiveInvalidToolCallsPerTool` are the node-global tool-pipeline ceilings that the DI-wired
`FunctionInvokingChatClient` and `ToolArgumentRepairAIFunction` apply; they ride the record as read-only
reference values, so one place documents the whole turn's bounds.

`WithEffectiveContext` folds the window a local model actually launched with back into the policy.
`RequestedContextTokens` is what makes that safe: a user-requested `num_ctx` is a ceiling to keep, while the
untrusted configured default must be **replaced** by the real launched window.

## 2.6 The coder reader

Continues [Agent Mode](../04-agent-mode.md#26-the-coder-reader), which keeps the section introduction.

That matters twice. A `find`/`grep` argument vector is POSIX-only: on a stock Windows 11 install `grep` does not exist and
`find` resolves to the DOS tool, which rejects the vector. And it puts the confinement in an argument list this class would
have to keep correct, rather than in the component that owns the jail. A typed search request carries the pattern as **data**,
so a value beginning with `-` cannot be read as a flag, and a model-supplied expression runs under a per-line timeout the
shell-out never had.

The **secret exclusions stay with the reader as caller policy**, deliberately. They are supplied to the provider so excluded
trees are pruned *before* the result budgets — a large `.git` baseline sorts ahead of project aliases and would otherwise
consume the whole cap — and then re-applied to the returned paths as defence in depth. The policy is broader than Development
Mode's: Coder drops its whole copy-filter set, not just credentials. The `read_file` post-filter gates on `IsSecret` only,
because a preserved workspace legitimately contains build output and refusing `bin`/`obj`/`node_modules` would cost an agent
real capability while protecting nothing.

Every result the model sees is fenced through `UntrustedContentFraming` with a per-call **random** nonce: workspace file
names, match lines and file content are all attacker-influenced, and a tool result is query-dynamic rather than a
prompt-cache-stable prefix. Node-authored lead lines and truncation notices stay outside the fence.

## 2.7 Post-run adaptive memory: the extraction worker's shutdown contract

Continues [Agent Mode](../04-agent-mode.md#27-post-run-adaptive-memory-the-extraction-workers-shutdown-contract), which keeps the section introduction.

**Shutdown drains QUEUED work as well as in-flight work**, because the queue is in-memory: a job dropped at shutdown is
lost for good, where an in-flight one merely finishes late. `StopAsync` therefore completes the writer, waits out
`ShutdownDrainTimeoutSeconds`, then cancels the drain deadline and allows a brief fixed `PostDeadlineGrace` for the
read loop and any cancelled straggler to unwind before disposal. That grace is a **cap, not a wait**: a job that
observes its token finishes well inside it, and the bound only binds one that ignores it.

Past the grace a job is **abandoned** — a deliberate trade, not an oversight. An abandoned job may go on to observe
already-disposed host services, so it is counted on `NodeMetrics.MemoryExtractionAbandonedTotal` rather than silently
dropped, and every failure logs the exception **type name only** (extraction runs over conversation content, so a
message would put transcript text in the log). Extraction stays disabled for a spawned sub-agent, as §2.3 notes.
