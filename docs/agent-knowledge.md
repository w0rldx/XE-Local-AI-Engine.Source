# Agent Knowledge Base

Hard-won rules, invariants and traps for this repository. `docs/wiki/` explains how the system is built; these files
record failures that are easy to repeat even after reading the code.

**How to use:** always read §0 and the routing table below. Then open only the topic files your change touches, and
their `## Stale beliefs` tables. The [evidence ledger](agent-knowledge-evidence.md) holds measurements and incident
history; it is not required reading. Follow it when changing a rule or diagnosing the same failure. Treat these files
as a floor, not proof that no other invariant exists.

## 0. Repo orientation

- **Rule:** discover this checkout's remotes with `git remote -v` and branch tracking with `git branch -vv`; never
  assume a remote name or URL. **Prevents:** fetch, review or integration commands aimed at a remote copied from
  another checkout. **Authority:** the current Git configuration; `AGENTS.md` (branch from and target `develop`).
- Old `C0re.*` solution, project and Docker-context names are gone; current names: `docs/wiki/02-project-layout.md`.

### Cite symbols, not `file:line`, for anything under active development

**Rule:** cite a file plus a symbol or quoted phrase; never a line inside a file your change edits. Symbols are cited by
name, never by line number. **Prevents:** line citations that stay in range after edits while pointing at the wrong
code. **Authority:** `AGENTS.md`; ADR 0004 anchor-maintenance note. [evidence](agent-knowledge-evidence.md#why-symbol-citations-replaced-line-citations)

### Describe the environment generically — this is a public repository

**Rule:** docs, comments and fixtures describe the environment in general terms: no installed-tool patch versions, no
uid-bearing socket paths or subuid ranges, no host RAM/CPU inventory, no "this box" phrasing. GPU model, VRAM and
driver appear only as metadata of a dated measurement; repo-pinned versions (llama.cpp tag, SDK, package pins) are
fine. **Prevents:** re-leaking the maintainer's machine into a public repository (it happened after a cleanup had
already removed it once). **Authority:** maintainer decision 2026-09-11.

### After rebasing over a comment-only refactor, re-read the `<remarks>` that merged cleanly

**Rule:** a comment-shortening commit usually moves prose (from `<summary>` into `<remarks>` or a `//` run). After
resolving the conflict, re-read every `<remarks>` and comment run in each conflicted file, not only the hunk.
**Prevents:** a silently merged block keeping the exact claim your branch exists to correct. **Authority:** the
2026-09-20 feature-audit rebase (`ApiToolCallBridge`, `PendingToolCallRegistry`).

## Read when

| You are changing | Read |
|---|---|
| `.editorconfig`, `Directory.Build.*`, `Directory.Packages.props`, analyzer suppressions, `Architecture/` tests, DI modules; Release red where Debug was green | [build-and-analyzers](agent-knowledge/build-and-analyzers.md) |
| Writing, scoping or debugging a backend test; `TestServerWebAppFactory`, `scripts/run-tests-memory-safe.sh`, the build lock, browser E2E host | [backend-tests](agent-knowledge/backend-tests.md) |
| `.github/workflows/**`, `HEAVY`/sharding, coverage merge, `publish/**`, `scripts/release/**`, `cliff.toml`, Velopack bootstrap; committing in a shared worktree | [ci-and-release](agent-knowledge/ci-and-release.md) |
| Running or scripting a dev host or live round; `Program.cs`/Kestrel/auth, HTTP clients, sandbox host side, Windows-only branches | [dev-runtime](agent-knowledge/dev-runtime.md) |
| `Providers.LlamaServer`, the llama.cpp pin or binaries, launch fallback, tool offering or a tool schema, chat-template capability detection | [inference-runtime](agent-knowledge/inference-runtime.md) |
| Model fit / HF discovery, `CapacityService`, llama-server supervisor, `Benchmark*`, knowledge base, `Services/Training`, `Services/Transcription` | [models-and-inference](agent-knowledge/models-and-inference.md) |
| `AI.Agent`, tool invocation or approval, cloud/MCP providers, Graph/Dev Workflows, `LaunchMode.McpOnly`, MAF/MEAI/`OpenAI` bumps | [agents-and-sandbox](agent-knowledge/agents-and-sandbox.md) |
| `Services/Sandbox`/`Compute`/`AgentHome`, bubblewrap, any path-containment or patch-apply check | [sandbox-and-compute](agent-knowledge/sandbox-and-compute.md) |
| `XE-Local-AI-Engine.Client.React/`, an endpoint's request/response/error declaration, hub payloads, `openapi/v1.json` | [frontend-and-api](agent-knowledge/frontend-and-api.md) |

## 1. Build, test, CI, packaging

`AGENTS.md` ("Validation") is authoritative for commands and CI shape; the traps behind it are in
[build-and-analyzers](agent-knowledge/build-and-analyzers.md), [backend-tests](agent-knowledge/backend-tests.md) and
[ci-and-release](agent-knowledge/ci-and-release.md).

## 2. Dev environment & local runtime

Dev host lifecycle, node secret and data dirs, the shared model store, live rounds, sandbox host side, Windows:
[dev-runtime](agent-knowledge/dev-runtime.md). llama.cpp binaries and launch fallback, tool-call gates:
[inference-runtime](agent-knowledge/inference-runtime.md).

## 3. Models, inference, retrieval

Discovery, capacity and spawn, benchmarks, knowledge base, training, transcription:
[models-and-inference](agent-knowledge/models-and-inference.md). The tool-grammar repetition ceiling and the arithmetic/time
tools left by a partial offer (the partial-failure residue), cited in code as §3, live in
[inference-runtime](agent-knowledge/inference-runtime.md) ("Passing all five gates ..." and "Capability detection ...").

## 4. Agent Mode, MAF, sandbox, cloud providers

MAF/MEAI, tools and approval, cloud providers, workflow engines: [agents-and-sandbox](agent-knowledge/agents-and-sandbox.md).
Sandbox, compute and AgentHome containment: [sandbox-and-compute](agent-knowledge/sandbox-and-compute.md).

## 5. Frontend, chat UX, API boundary

SPA contracts, OpenAPI/hey-api, endpoint error shapes, frontend tests and tooling:
[frontend-and-api](agent-knowledge/frontend-and-api.md).

## 6. Deliberately NOT built

Do not assume these exist or "restore" retired designs.

- **Budgeting and compaction are two mechanisms.** Per-turn/per-round budgeters (`ConversationContextBudgeter`,
  `ProviderCallBudgetChatClient`) excerpt/drop history; cross-turn compaction (`ConversationSummarizer` +
  `ConversationCompactionService`, `LocalApiRoutes.CompactConversation`) folds on a node-local model only. One fold
  can exceed `HttpNetworkTimeout` (per network operation, not cumulative) and 500 cleanly, leaving compaction columns
  NULL; that 500 is one fold, not eight folds exhausting a budget.
- **Restart reconciles, never auto-resumes.** `InvocationResumeRegistry` is browser-reconnect memory; Regenerate is
  the recovery. **No mid-stream retry** (pre-first-token retry and circuit breaker exist).
- **Orchestration is handoff-only.** Group-chat/Magentic/sequential/concurrent builders are deferred (ruling
  2026-09-09); `OrchestrationAgentFactory` calls only `CreateHandoffBuilderWith`.
- **Approval policy is tighten-only;** category-wide loosening is not built. The scheduler runs a saved local
  single agent, strips approval tools, writes no conversation and compiles no orchestration.
- **Sandbox providers:** fake, process, opt-in Development Docker. OpenSandbox is not built and nothing selects it or MXC
  silently; Development Docker is interim, not a hard security boundary (ADR 0004,
  `docs/roadmaps/development-mode-container-status.md`).
- **Playbook retrieval** is embedding-ranked with lexical fallback; adaptive memory is per-agent only.
- **No background refresh** of the cloud chat-client selection snapshot (`ActiveCloudChatClientFactory`); the
  `MA0045` pragmas mark the sync fill.
- **No RAG over chat attachments,** no image/OCR ingestion. **STT ships** (whisper.cpp, wiki 24); TTS is browser Web
  Speech and Kokoro is not shipped.
- **Open Canvas (Preview) is removed** and not coming back (wiki 21 §9); do not restore `PreviewWorkflow*`,
  `features/preview/`, `preview/*` routes or `canvas_workflows`. Desktop-only ThemeConfigurator stays outside the
  mobile-responsive scope.

## 7. Agentic support / MCP-only mode

`--mcp-only` launch, ready output, delegate/agentic key authority, the skill source and the MCP reference drift test:
[agents-and-sandbox](agent-knowledge/agents-and-sandbox.md) ("Agentic support / MCP-only mode").

## Stale beliefs

Superseded claims are corrected in a `## Stale beliefs` table at the end of each topic file; read the one for the area
you touch.

## Maintenance

- **Entry format:** `### <heading>` then one paragraph `**Rule:** … **Prevents:** … **Authority:** <file + symbol |
  test | script>.`, optionally an evidence link. Body <= 900 chars. Keep an existing heading verbatim: code comments
  quote headings and cite `docs/agent-knowledge.md §N`. Narrative, measurements and incident history go to the
  [evidence ledger](agent-knowledge-evidence.md) under the matching area heading.
- **New proposals** go to [proposed](agent-knowledge/proposed.md), which is not required reading. Promotion needs
  operator approval; the entry then moves to its topic file.
- `scripts/docs-inventory-check.py` (CI `python-quality`) enforces the caps: index <= 12 KB with `## 0.`…`## 7.`,
  every topic file linked here, topic file <= 32 KB, entry body <= 900 chars, no `PROPOSED` heading outside
  `proposed.md`.
