# React Client (Frontend)

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

The React management UI lives in `XE-Local-AI-Engine.Client.React` and is the operator console for a single node: chat, agent mode, model management/advisor, scheduler, MCP, skills, settings, and dashboards. It is a Vite + React 19 + Mantine SPA, served same-origin from the Node Web Server's `wwwroot`. All server state flows through TanStack Query over a **generated hey-api SDK** that is the single source of truth for REST; SignalR drives the streaming/live surfaces. This page maps the directory layout, the state strategy, the transport plumbing, the shared UI primitives, i18n, and how the bundle is hosted.

> Discrepancy note vs. ground truth: `useShallow` is **not** used anywhere in the current source (a `grep -rn "useShallow"` returns nothing). Forms are **not** built on a schema-bound form library; they are controlled Mantine inputs validated by hand-authored Zod schemas via `safeParse`. `@tanstack/react-form` is a dependency but only its **devtools panel** is wired (`core/dev-tools/components/DevelopmentUi/DevelopmentUi.tsx`). The code is documented as-is below.

---

## Stack at a glance

Source: `XE-Local-AI-Engine.Client.React/package.json`.

| Concern | Library | Notes |
|---|---|---|
| Framework | `react` / `react-dom` 19 | Uses React 19 `use()` (e.g. `DialogShell` reads context with `use(ConfirmContext)`). |
| Build/dev | `vite` 8, `@vitejs/plugin-react` | `pnpm dev` → port 5173; `pnpm start` → 3000. |
| UI kit | `@mantine/core` / `hooks` / `notifications` | Modals, inputs, layout, toasts. |
| Routing | `@tanstack/react-router` | File/route tree via `@tanstack/router-plugin`. |
| Server state | `@tanstack/react-query` 5 | All REST reads/mutations. |
| UI/session state | `zustand` 5 | Auth token, theme, sidebar, language, dev mode, pagination prefs. |
| HTTP | `axios` 1 | One shared instance behind the generated client. |
| Validation | `zod` 4 | Boundary parsing of API responses + form schemas. |
| Realtime | `@microsoft/signalr` 10 | Shared connections for the hub paths the backend maps (chat, scheduler, preview, benchmark runs, dataset generation, training runtime, training runs, GGUF download, CUDA build, llama.cpp source build, runtime acquisition, knowledge base, image jobs, stable-diffusion.cpp source build) plus Development attempts when enabled. The `MapHub<>` calls in `Program.cs` are the inventory — see [API & Hubs](09-api-and-hubs.md). |
| Markdown | `react-markdown` + `remark-gfm` + `react-syntax-highlighter` | Chat + editor rendering. |
| i18n | `i18next` + `react-i18next` + browser language detector | `en` / `de`. |
| Canvas | `@xyflow/react` | Preview workflow builder (see [Agent Mode](04-agent-mode.md) / Preview). |
| Voice / TTS | Browser Web Speech API | Text-to-speech through browser/operating-system voices; availability and network behavior are platform-controlled. No TTS npm runtime or voice-model download. |
| Onboarding tour | `react-joyride` | Guided first-response walkthrough (see `features/onboarding`). |

Tooling gates (`pnpm build` / `pnpm lint`): `tsc --noEmit`, a custom `scripts/CheckEventCurrentTargetInUpdaters.mjs` guard, Biome lint, Stylelint, plus `knip` and `dependency-cruiser` in `pnpm validate`. Knip fingerprints the current unused surface and enforces strict no-growth; reducing that surface passes without a baseline edit. Exact-pinned React Doctor is available separately through `pnpm run doctor` as an offline advisory and is intentionally outside `validate` and CI; `REACT-DOCTOR.md` records its license and compatibility evidence.

---

## Entry point & provider stack

`index.html` loads `/src/Main.tsx`; `Main.tsx` mounts `App.tsx`. The provider nesting (`src/App.tsx`) establishes the app-wide contexts:

```
ThemeProvider                 (Mantine + theme store)
└─ TanStackQueryProvider      (QueryClient)
   └─ ConfirmProvider         (promise-based confirm dialogs)
      └─ ErrorBoundary        (react-error-boundary → AppErrorFallback)
         └─ RouterProvider    (TanStack Router)
```

**Text fallback:** theme state wraps the React Query client; confirmation dialogs and the global
error boundary sit inside that data layer; the router is the innermost application provider.

i18n is initialized as a side-effect import (`src/i18n.ts`) and `dayjs` is extended with the UTC plugin at module load.

---

## Directory layout

Two trees carry essentially all the code:

- **`src/core/`** — cross-cutting infrastructure: API/transport (`core/api`, incl. `core/api/signalr`), auth (`core/auth`), routing/query integrations (`core/integrations`), layout/navigation (`core/layout`), theme (`core/theme`), locales plumbing (`core/locales`), dev tools (`core/dev-tools`), frontend diagnostics collection + redaction (`core/diagnostics`), the shared UI library (`core/ui`), and the browser voice runtime (`core/runtime` — `TtsProvider`, `VoiceRuntime`, and `CapabilityDetector`, consumed by `features/voice`, all backed by Web Speech rather than a bundled model or worker).
- **`src/features/`** — one folder per product area, each self-contained with `pages/`, `components/`, `queries/` (TanStack Query hooks), `models/` (DTO ↔ view mappers + Zod), and optionally `stores/` and `hooks/`.

Seven smaller siblings exist and are worth knowing before you put something in the wrong place: `src/capabilities/` (the central `nodeRoutePaths` + `nodeCapabilities` declaration), `src/routes/` + the generated `routeTree.gen.ts` (TanStack Router file routes — thin, one `createFileRoute` per page), `src/data/` (static navigation and language menu data), `src/components/` (the logo marks), `src/modules/` (the standalone theme configurator), `src/locales/` (the bundled `en`/`de` JSON), and `src/pages/` (the `Home` landing page).

### The feature folders

Source: the first-level directories under `XE-Local-AI-Engine.Client.React/src/features` — `ls -d src/features/*/` is the inventory; the table below names all of them.

| Feature | What it owns | Deep-dive |
|---|---|---|
| `about` | About dialog: build/version (from `eng/ReleaseVersion.props`, imported by `Directory.Build.props`) + an auto-generated third-party license list (`data/third-party-licenses.generated.json`, npm + NuGet) | — |
| `agents` | Agent definitions, templates, playbooks, golden conversations, feedback insights, execution logs, orchestration topology | [Agent Mode](04-agent-mode.md) |
| `api-foundation` | Validation-problem probe (boundary/error-contract harness) | [API & Hubs](09-api-and-hubs.md) |
| `app-update` | Velopack self-update UI: anonymous public-feed check and apply | [Hosting & Deployment](11-hosting-and-deployment.md) |
| `assist` | AI-assisted drafting: the `GenerationAssistDialog` + `AssistActions` affordance that drafts an agent definition or a skill body from a local model, and the `useAssistDraft` query behind it. Rendered inside the agent and skill authoring forms rather than owning a route | [Agent Mode](04-agent-mode.md) |
| `benchmarks` | Benchmark projects and runs at `/benchmarks`: project form, run pane and live pane (`useBenchmarkRunHub` — subscribe-then-replay, dedupe on sequence), status badges, manual score picker, judge panel, and the launch-evidence/receipt comparison views | [API & Hubs](09-api-and-hubs.md) |
| `binding` | Node binding to the C0re platform | [Security & Privacy](12-security-and-privacy.md) |
| `chat` | Streaming chat UI, SignalR adapter, reasoning/tool-call rendering, sampling options, file-upload attachments + pane drag-and-drop (`usePaneFileDrop`, `ChatAttachmentChips`) | [Chat](05-chat.md) |
| `cloud-settings` | Cloud provider credentials/config (kept node-local) | [Security & Privacy](12-security-and-privacy.md) |
| `commands` | Automation-command catalog backed by `automation/commands`; list/detail queries use the generated SDK | [API & Hubs](09-api-and-hubs.md) |
| `customTools` | Operator-authored HTTP-fetch and host-program tools: list, create/edit form, danger acknowledgement, enabled state, secret header/env masking, and desktop executable probe | [Security & Privacy](12-security-and-privacy.md#custom-tools-operator-authored-execution-boundary) |
| `dashboard` | Node overview surface | — |
| `development` | **Development Mode** operator surface at `/development`, surfaced under the "Preview" nav group (alongside Open Canvas and Image Generation) because it is an experimental surface: project creation bound to a registered Git repository (`DevelopmentProjectForm`), the task/attempt workflow, and `DevelopmentLivePanel` streaming live attempt output over `DevelopmentAttemptHub` (`hooks/useDevelopmentAttemptHub.ts`). The page resolves the authenticated server capability (`development/capability`) before exposing projects or actions, so a backend kill switch fails closed without a separate frontend build | [Architecture Overview](01-architecture-overview.md#development-mode-registered-source-managed-worktree) |
| `devWorkflows` | **Development Workflows** at `/development-workflows` and `/development-workflows/{workItemId}`: the work-item list and create dialog, and a detail page whose run, node and tab selections live in **search params** rather than the path, so a view is linkable. Owns `DevWorkflowNodeRunTable`, the gate/intervention panel (approve, reject, request changes; retry, skip, abandon), the artifact and event tabs, the **rule-set catalogue** and the **definition form editor** (both tabs on the list page: `DevWorkflowRuleSetsPanel`/`DevWorkflowRuleSetDialog` write the policy documents a run injects into a node's objective, `DevWorkflowDefinitionFormPanel` edits a template's nodes and edges against `DevWorkflowDefinitionValidation`, a pure mirror of the graph rules the form can break), and `useDevWorkflowRunHub` — one `devWorkflowChanged` notification invalidates the run's queries, so the hub never carries state the REST reads do not. The event feed pages **forward by sequence watermark** with `useInfiniteQuery`; sequences are not contiguous, so the cursor is the last sequence seen, never a row count | [API & Hubs](09-api-and-hubs.md) |
| `external-providers` | Operator-registered external OpenAI-compatible endpoints at `/external-providers`: the connection list plus its editor — display name, base URL, the declared Local/Cloud trust (with the warning shown when Local is declared for a host that is neither loopback nor private), a write-only API key with an explicit "Remove key" action, and the timeout — the per-model registration rows (backing model id, display name, context length, declared tools/vision/reasoning capabilities and a reasoning-effort default), and the connect-time probe with pick-to-add. Writes are optimistic-concurrency guarded: a 409 carries the whole stored configuration, which the page renders in place of a refetch | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `diagnostics` | **Local-only** frontend error diagnostics at `/diagnostics` — the *only* feature with no backend endpoint at all. `SnapshotStore.ts` is an **IndexedDB** store (`idb`, DB `xe-diagnostics`) holding at most 25 snapshots / 25 MB, with retention enforced *inside* the same readwrite transaction as the write so eviction is atomic. `DiagnosticsPanel` lists/inspects snapshots; `BreadcrumbTimeline`, `NetworkLog` and `RrwebReplay` render one; `ExportSnapshot` writes a bundle to disk and import reads one back. Reached from the header/mobile nav "report a problem" action (`ReportProblemButton`). Collectors and redaction live in `core/diagnostics/`. **Nothing here is ever transmitted** | — |
| `images` | Local text-to-image at `/images`, surfaced under the "Preview" nav group (alongside Open Canvas and Development Mode) because the runtime is not yet confidently verified end-to-end: generation form, job list/cards, result view, installed-model manager, plus `useImageJobHub` (subscribe-then-replay, dedupe on `seq`) and `useImageObjectUrl` (fetches the encrypted PNG as a `Blob`) | [Image Generation](14-image-generation.md) |
| `integrations` | **External Integrations** operator surface: `/integrations/triggers` (the named external entry points that run a saved agent unattended over the loopback integration API — list plus an editor dialog whose `name` slug is validated live because it is the integrator's URL) and `/integrations/keys` (the `xeint_` credentials, with a show-once reveal held in component state and never in a store or the query cache, a `principalId` identity column and an identity picker for rotating a credential without stranding an integrator's sessions). The trigger editor derives its approval warning and its `CallerManaged` preflight CLIENT-side from `listAgentDefinitions` ∩ `getToolCatalog`, tighten-only and **fail-closed**: a tool name absent from the live catalog counts as approval-requiring and as side-effecting. The allowlist switch is the only thing that produces the all-triggers wildcard — an empty multiselect is a validation error, never a grant. The group's index route only redirects. `/integrations/executions` lists active and historical runs with one status chip per state, a cancel action on active rows and a detail dialog whose timeline reads the persisted events whole; `/integrations/sessions` lists the caller-managed conversations with both filters server-side and a delete that refuses while an execution is still active. Both lists are a single bounded server window with no page navigator, because neither response carries a total count | [API & Hubs](09-api-and-hubs.md) |
| `invocations` | Tool/function invocation history | [Agent Mode](04-agent-mode.md) |
| `knowledge` | Knowledge Base / RAG at `/knowledge-base`: document upload panel + table, status badges, last-known-good badge, document drawer, and the hybrid search panel; live indexing status via `useKnowledgeBaseHub` | [Knowledge Base / RAG](15-knowledge-base.md) |
| `loaded-models` | Live loaded-model overview + graceful eject | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `mcp` | MCP server registration + tooling | [API & Hubs](09-api-and-hubs.md) |
| `model-fit` | Box-aware GGUF recommendation + quant pick, plus the per-machine inference-profile panel (`InferenceProfilePanel`, explore/benchmark/freeze) | [Model Fit](07-model-fit.md) |
| `models` | Model management (HF GGUF discovery/download, classification) | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `node-settings` | User-editable cached node settings, local runtime config, the **runtime build cards**, and `RuntimeAcquisitionBanner`: `useRuntimeAcquisitionHub` hydrates `GET model-fit/llamacpp/acquisition`, merges `runtimeAcquisition.statusChanged` pushes by monotonic sequence, and rehydrates after reconnect | [Hosting & Deployment](11-hosting-and-deployment.md), [Local Runtime & Providers](03-local-runtime-and-providers.md#26-in-app-source-builds-linux) |
| `onboarding` | First-response guided tour (React Joyride) + welcome dialog with language picker + showcase panel | — |
| `preview` | Open Canvas (Preview) workflow builder (React Flow); surfaced under the "Preview" nav group | [Agent Mode](04-agent-mode.md) |
| `scheduler` | Quartz job management + run history | [Scheduler](06-scheduler.md) |
| `skills` | Node skill library + per-agent skill picklist | [Agent Mode](04-agent-mode.md) |
| `tools` | Tool catalog / capability surface | [Agent Mode](04-agent-mode.md) |
| `training` | QLoRA fine-tuning at `/training`, `/training/datasets` and `/training/comparisons` (three **sibling** file routes, each with its own `beforeLoad` capability gate): the runtime install card, dataset definition editor and sample review, the run wizard and run list, the artifact panel, comparison creation, and three hubs (`useDatasetGenerationHub`, `useTrainingRunHub`, `useTrainingRuntimeHub`) | [Training](18-training.md) |
| `usage-dashboard` | Agent token-usage dashboard at `/usage`, backed by the single `agents/usage-summary` endpoint. `models/UsageDashboardModel.ts` does all the aggregation **client-side** — `aggregateByDay` / `aggregateByModel`, `clampDateRange` against the server-reported retention floor (30-day fallback) — feeding `UsageTotalsCards`, `UsageDailyChart`, `UsageProviderBreakdown` and `UsageModelTable`. Like `/invocations` it carries no extra route guard: the authenticated `_layout` *is* the operator gate and the endpoint 401s otherwise | [Agent Mode](04-agent-mode.md) |
| `voice` | Web Speech text-to-speech: node-settings feature gate, platform-voice catalog, preferences store, composer controls, per-message play button, and voice preview. Browser/OS voice behavior is outside repository control. | [Chat](05-chat.md) |
| `workSessions` | Long-running agent work sessions at `/work-sessions` and `/work-sessions/{id}`: the list + create dialog, and a 3-pane detail page whose centre pane **is** `features/chat`'s `Chat` component. Owns `useWorkSessionHub` and the query hooks over the 16 generated work-session operations. The one feature that deliberately composes another feature's page — see [Work Sessions: the chat-embed seam](#work-sessions-the-chat-embed-seam) | [Agent Mode](04-agent-mode.md) |

Each feature follows the same shape, e.g. `features/agents/` has `pages/AgentsPage.tsx`, `components/*` (forms, panels, gallery), `queries/use*.ts` (TanStack Query hooks), `models/*Models.ts` + `*Mappers.ts` (Zod + DTO mapping), and `stores/AgentManagementStore.ts`. Two features deviate deliberately: `diagnostics` is flat (no `pages/`, no `queries/` — it has no backend to query), and `about` renders as a dialog rather than a route.

### Feature ↔ route ↔ capability

Route paths and their capability flags are declared centrally in `src/capabilities/NodeCapabilities.ts` (`nodeRoutePaths`, `nodeCapabilities`), not scattered across route files. Capability-flagged pages (`agents`, `skills`, `mcp`, `scheduler`, `modelFit`, `loadedModels`, `preview`, `knowledgeBase`, `images`, `development`, `benchmarks`, `training`, `workSessions`, `devWorkflows`, `integrations`) are all **on** by default; Custom Tools shares the `agentManagement` capability, while `commands`, `usage`, and `diagnostics` are authenticated but otherwise ungated. `development` additionally re-checks the *server* capability at runtime — a frontend flag alone is not treated as authoritative.

---

## State strategy

Two stores, strictly separated:

1. **Server state → TanStack Query.** Every REST read/write is a query/mutation hook under a feature's `queries/` folder (e.g. `features/agents/queries/usePlaybookActions.ts`, `features/skills/queries/useSkills.ts`). The `QueryClient` is provided by `core/integrations/tanstack-query/Provider.tsx`. Server data is **not** mirrored into Zustand.
2. **UI/session state → Zustand.** `find src -name "*Store.ts*"` is the inventory (a labelled snapshot: **24** files on 2026-08-17, **23** of them Zustand stores) — e.g. `core/auth/stores/NodeAuthStore.tsx` (access token + actions), `core/theme/stores/ThemeStore.tsx`, `core/layout/stores/SidebarStore.tsx` + `DesktopNavigationBarStore.tsx`, `core/locales/stores/UserLanguageStore.tsx`, `core/dev-tools/stores/DeveloperModeStore.ts`, and `core/ui/components/TablePagination/useTablePaginationStore.ts`. The one exception, `features/diagnostics/SnapshotStore.ts`, is **not** Zustand — it is the IndexedDB snapshot store and imports no state library. Don't include it when reasoning about the Zustand surface.

Stores use the plain `create<T>()` factory with an `actions` sub-object (see `NodeAuthStore.tsx`). Components subscribe with field selectors (`useNodeAuthStore((s) => s.accessToken)`); `useShallow` is not currently in use, so object/array selectors must be hand-shaped to avoid re-render churn — a maintainer adding a multi-field selector should prefer separate selectors or introduce `useShallow` at that point.

---

## Transport: the generated hey-api client layer

Full transport contract and hub mapping live in [API & Hubs](09-api-and-hubs.md). The frontend half:

### Single shared axios instance

`core/api/axios/AxiosInstance.ts` creates **one** axios instance. The `baseURL` is intentionally the empty string `""` (same-origin relative), and the comment is load-bearing:

```ts
// "" keeps both the generated SDK and the hand-wrapped (buildLocalApiUrl) calls same-origin.
// "/" here would make hey-api emit "//api/local/v1/<path>" — a protocol-relative URL the
// browser resolves against host "api" (ERR_NAME_NOT_RESOLVED, request hangs forever).
baseURL: "",
withCredentials: true,
```

### Generated SDK wiring

`core/api/Generated.runtime.ts` supplies `createClientConfig` to the generated client: it injects the shared `axiosInstance`, sets `baseURL: ""`, and `throwOnError: true`. Because FastEndpoints emits OpenAPI paths that **already include** `/api/local/v1`, the generated SDK calls the host root; hand-written helpers use `buildLocalApiUrl()` (`core/api/utils/LocalApiUrl.ts`) to prepend `/api/local/${VITE_API_VERSION}`.

### Generated artifacts (do not hand-edit)

Under `core/api/generated/` (regenerated by `pnpm openapi`):

- `types.gen.ts` — request/response DTO types.
- `sdk.gen.ts` — typed operation functions.
- `@tanstack/react-query.gen.ts` — query/mutation option factories.
- `zod.gen.ts` — Zod schemas mirroring the DTOs.
- `client/`, `core/` — hey-api runtime (axios adapter, serializers, SSE).

`pnpm openapi:check` fails if the generated output drifts from the committed OpenAPI doc — the backend OpenAPI document is the single source of truth.

### SignalR: one shared connection per hub

Hubs are not in OpenAPI, so the client half is hand-written — but not per-component. `core/api/signalr/SharedHubConnection.ts` is a **refcounted module-level manager: one `HubConnection` per hub path**, reused across mounts. The first `acquire` builds and starts it, later acquires reuse it, the last `release` stops it — replacing the older pattern where every feature hook built a fresh connection in its mount effect and each page visit paid a full negotiate + WebSocket upgrade.

Two properties a maintainer must preserve:

- **Handlers stay per-subscriber.** The manager owns only the connection *lifetime*; each hook still calls `connection.on(...)` in its effect and `connection.off(...)` with the **same handler reference** on cleanup. SignalR fans a client method out to every registered handler, so concurrent subscribers to one hub do not steal each other's events.
- **StrictMode / fast-remount safety.** The stop is deferred until the start promise settles *and* re-checks the refcount, so an acquire → release → acquire flip (React's double-invoke, or a quick navigate-back) never aborts an in-flight negotiation nor tears down a connection a new subscriber has already taken over.

`accessTokenFactory` reads the *current* store token on the initial negotiate and on every automatic reconnect, so a long-lived shared connection re-authenticates across reconnects; on logout the holding pages unmount, the refcount drops to zero and the connection stops. Documented parity gap: unlike `NodeChatConnection`, this factory does **not** proactively refresh a near-expiry token — neither did the per-mount hooks it replaced.

**Runtime acquisition is hydrate + push, not push-only.** `RuntimeAcquisitionBanner` is mounted in the authenticated
layout. `useRuntimeAcquisitionHub` first reads the generated `getRuntimeAcquisitionStatusOptions()` query, subscribes
to `runtimeAcquisition.statusChanged`, and applies `keepLatestAcquisitionStatus` to both paths. The monotonic sequence
rule prevents a late GET from overwriting a newer push; reconnect invalidates the query because acquisition normally
starts before the operator has logged in and may finish while the socket is disconnected.

**Custom Tools use the generated REST layer end to end.** `features/customTools/queries/useCustomTools.ts` wraps the
generated list/get/create/update/delete and executable-probe options with `withResponseValidation`, maps generated
optional DTOs to strict feature models, and invalidates both collection and detail query keys after writes. The
Zustand `CustomToolManagementStore` holds only editor/dialog state; definitions remain TanStack Query server state.
Secret fields returned as the mask sentinel are rendered as masked and may be round-tripped, never placed into a
second browser-side secret store.

### Interceptors & 401 handling

`core/api/axios/Interceptors.ts` registers four interceptors on the shared instance:

| Interceptor | Behavior |
|---|---|
| `addAuthRequestInterceptor` | Attaches `Authorization: Bearer <token>` from `useNodeAuthStore` on every request. |
| `addUnauthorizedErrorInterceptor` | On `401`: refresh once via `refreshNodeAuthToken()`, set the new token, replay the original request (tracked in a `WeakSet` so it never loops). On refresh failure or a second 401: clear auth and `redirectToLoginOnce()`. |
| `addRateLimitingInterceptor` | On `429`: shows a translated toast (`errorMessages.tooManyRequests`). |
| `addApiProblemDetailsInterceptor` | Maps non-2xx/401/429 responses to a typed `ApiError(status, ProblemDetails)`; turns `ERR_NETWORK` into a `"Network error"`. |

The 401 refresh/redirect path is deliberately isolated: it calls a dedicated `refreshNodeAuthToken()` (not a generic SDK call) and guards redirects with `isRedirectingToLogin` plus a `/login`/`/setup` short-circuit, so it cannot recurse through the same interceptor. Response boundary parsing uses Zod (`core/api/ResponseValidation.ts`) — never trust raw server JSON shape.

---

## Work Sessions: the chat-embed seam

`features/workSessions/` owns two file routes — `routes/_layout/work-sessions.index.tsx` (`/work-sessions`, list +
create dialog) and `routes/_layout/work-sessions.$sessionId.tsx` (`/work-sessions/{id}`, the 3-pane detail page).
Both are thin adapters with a `beforeLoad` gate on `nodeCapabilities.workSessions`; the page components stay
router-free. Note that the node *also* has its own `WorkSessions:Enabled` switch which 404s the API — the frontend
flag only decides whether the surface is offered.

The detail page is `FullHeightPage` → a fixed grid `320px | 1fr | minmax(380px, 420px)`: the plan/task tree with the
Start/Pause/Resume/Cancel controls, the session's own conversation, and a Findings | Artifacts | Checkpoints | Events
panel. Below **1024px** it collapses to the conversation with the two side panes in `Drawer`s — the same breakpoint,
for the same reason, that `ChatDisplayShell.tsx` records.

### The centre pane IS `Chat`, via one optional `scope` prop

A work session owns a conversation, and its **supervisor is the single writer of invocations on it**. Everything that
makes a conversation work — the `LocalChatHub` readiness gate, the rAF-batched stream commit, the tool timeline, and
decisively the cold-load `resumeActiveTurn` re-attach — lives in `Chat.tsx`. A wrapper would have to re-implement all
of it or ship a session view that cannot stream, so `Chat` instead takes one optional `ChatScope`
(`features/chat/models/ChatModels.ts`). `/chat` passes nothing and behaves exactly as before; `Chat.scope.test.tsx` is
the regression guard for that.

Under a scope, `Chat`:

- pins `selectedConversationId` to `scope.conversationId` and makes **every** write to the global chat preference
  store a no-op — otherwise opening a session would rewrite the operator's remembered `/chat` thread;
- hides the conversation column outright (`ChatDisplayShellProps.hideConversationList`; `conversationListCollapsed`
  only shrinks it to an icon rail, which is wrong when there is no list to pick from);
- forces agent mode on with `scope.pinnedAgentId` and renders the agent **and** model selectors read-only (the
  session pins the agent, the agent pins the model);
- routes the composer through `scope.onSendOverride` → `POST work-sessions/{id}/messages`, never
  `nodeChatAdapter.sendMessage`, and maps the Stop button to Pause via `scope.onStopOverride`;
- drops regenerate / branch / feedback / rename / delete, and disables the composer once the session is terminal;
- renders bare under `scope.embedded` (the parent owns the `FullHeightPage` frame).

One related change reaches `ChatInputArea`: `onSend` may now return a promise, and the draft is cleared only when it
**resolves**. The scoped composer posts over REST, so a rejected follow-up (an over-cap 400) must stay on screen to
retry. `/chat` returns void and clears synchronously, unchanged.

Approvals and `ask_user` need nothing: `ToolCallCard` and `AskUserQuestionCard` own their own mutations and read the
capability gate from a module constant, so rendering `MessageParts` renders working controls wherever it is mounted.

### `useWorkSessionHub` contract

`hooks/useWorkSessionHub.ts` acquires `work-sessions/hub` through `SharedHubConnection` and borrows the *shape* of
`useDevelopmentAttemptHub` (connection-state machine, pre-snapshot buffering, `seq` dedupe, re-subscribe on
reconnect). Four things differ and must not be copied from that hook:

1. **`SubscribeSession(sessionId, afterSeq)` takes two arguments.** The hook keeps its highest seen `seq` in a ref and
   passes it on the first connect **and on every reconnect**. The hub is store-backed, not ring-buffered, so a client
   absent for days is served correctly and there is no `replayReset`.
2. **`kind` is lowercase on the wire** — `status | step | task | finding | artifact | checkpoint`. The lookup table
   uses those literals and `useWorkSessionHub.test.tsx` asserts them literally, because a casing slip is a **silent
   no-op**, not an error: every invalidation would simply stop firing. An unrecognised kind falls back to refreshing
   every feed rather than being dropped.
3. **The snapshot's replay is a watermark plus a "something changed", not cache seed data.** A replayed
   `WorkSessionEventResponse` carries an `eventType` (`StepStarted`, …), *not* a change kind, so the missed feeds
   cannot be derived from it — the store, not the client, is the replay authority. A non-empty
   replay or `replayTruncated: true` therefore triggers one full-feed refetch; pushed events, which do carry a kind,
   drive the fine-grained per-feed invalidation.
4. **`step` also invalidates the conversation query and bumps a `resumeNonce`** that feeds `scope.resumeNonce`, which
   re-arms `Chat`'s re-attach so each supervisor step streams live instead of back-filling from the query a beat
   later. This depends on the backend publishing `step` at step *start*, while the invocation is still resumable.

`UnsubscribeSession(sessionId)` runs before `hub.release()` on unmount. When the subscribe fails the hook reports
`connectionState: "unavailable"` and a 3 s `pollIntervalMs` that every query on the page picks up — a dimmed
"Polling" chip in the plan header, never a blocking error.

### Two gate baselines this feature moved

- **`config/dependency-baseline.json` gained seven `no-cross-feature` fingerprints.** The "keep cross-feature reuse in
  `core/`" rule below is real, and this feature is the recorded exception: the centre pane *is* the chat page, so
  `workSessions/*` importing `chat/pages/Chat.tsx`, `chat/models/ChatModels.ts`, `chat/components/AgentSelectorCard.tsx`,
  `chat/queries/NodeChatQueryKeys.ts` and `agents/queries/useAgentDefinitions.ts` is the design, not drift. Hoisting
  `Chat` into `core/` to satisfy the rule would be a far worse trade.
- **`config/bundle-budget.json` `applicationJavaScriptBytes` rose 4 192 000 → 4 252 000.** Measured: 4 145 967 bytes
  without the feature, 4 204 890 with it. The old budget had ~46 kB of headroom and this feature consumed all of it.
  The two routes are separately code-split (6.2 kB list, 25.0 kB detail) and Monaco stays in its own lazy chunk, so
  `lazyEditorJavaScriptBytes` is untouched.

### E2E: the seeder has to be put back

`XE-Local-AI-Engine.Tests.E2ETests/Tests/WorkSessionsPageE2ETests.cs` drives create → Start → `update_work_plan` →
`complete_work_session` against `FakeOllamaState.ToolCallScript`. The E2E host does a blanket
`services.RemoveAll<IHostedService>()`, which strips `WorkSessionAgentSeeder` — and the two seeded personas are the
**only** agents that can run a session, because the four state tools are held out of the general chat offer and the
agent-send intersection (offered ∩ allowed) drops them for any agent built through the UI. The factory therefore
re-adds that one hosted service, beside the existing `KnowledgeIngestionWorker` exception. The execution supervisor
itself survives the blanket removal (it is also registered as a plain singleton and its `StartAsync` is a no-op), so
a session started from the browser still runs.

---

## Forms (Zod, controlled inputs)

Forms are controlled Mantine inputs with a **hand-authored Zod schema validated on submit**, not a form-binding library. Pattern (`features/agents/components/AgentDefinitionForm.tsx`, `features/mcp/components/McpServerForm.tsx`):

- Local `useState` holds the form `values` and a `fieldErrors` record.
- A `*FormSchema` (Zod) is run with `schema.safeParse(values)` at submit; failures map issues into per-field error messages.
- `models/*Mappers.ts` converts between the form view-model and generated DTOs.

Maintainer note: keep validation in the schema (the form components are documented as "validate-then-submit; validation stays in the form"), and add new fields to the Zod schema + the mapper together so the DTO boundary stays typed.

---

## Shared UI library (`core/ui`)

### Unified dialog system

All modals route through `DialogShell` (`core/ui/components/DialogShell/DialogShell.tsx`) — the single modal primitive wrapping Mantine `Modal` with consistent overlay/blur, a `DialogTextTitleBar`, a fullscreen toggle, an autosizing scroll body, and an optional sticky `footer` slot.

- `confirmCloseWhen` makes the dialog undismissable by overlay/Escape and routes the title-bar close through the shared `ConfirmContext` (read tolerantly via React `use()` so the shell still works outside a `ConfirmProvider`). Use it for editors with unsaved/in-flight state.
- `useUnsavedChangesGuard` (`core/ui/hooks/useUnsavedChangesGuard.ts`) blocks **navigation** (and tab close) while a form is dirty, via TanStack Router's `useBlocker({ withResolver: true, enableBeforeUnload })` driving the same promise-based `useConfirm()` dialog. A `promptOpenRef` prevents the prompt re-opening on re-render. These two guards are complementary: `DialogShell.confirmCloseWhen` guards the close button; `useUnsavedChangesGuard` guards route changes.
- `MarkdownEditorField` and `MarkdownView` (`core/ui/components/Markdown*`) are the shared markdown editor/renderer; `CodeBlock` provides syntax-highlighted code (used in chat tool-call rendering — see [Chat](05-chat.md)).
- Toasts go through `core/ui/notifications/Toast.tsx` (Mantine notifications). `ConfirmProvider`/`useConfirm` is the promise-based confirmation primitive.

### Table pagination

`useTablePagination<T>` (`core/ui/components/TablePagination/useTablePagination.ts`) is **client-side** pagination over an already-loaded list (default page size **25**, options `[10,25,50,100]`). Key design points a maintainer must respect:

- The full (filtered) array is the source of truth; the active page is **derived-clamped every render** (`Math.min(Math.max(1, requestedPage), pageCount)`) rather than reset via an effect — so a polling query whose array identity changes on refetch does not reset the page, but a shrinking filter still clamps in range.
- `storageKey` persists the chosen **page size** (not the active page) across reloads via `useTablePaginationStore` (Zustand + localStorage), keyed per table.
- `TablePaginationFooter` is the matching footer component.

---

## i18n

`src/i18n.ts` initializes `i18next` with `react-i18next` and the browser language detector. Resources are bundled JSON: `src/locales/en.json` and `src/locales/de.json`, with `fallbackLng: "en"` and language persisted in `localStorage` (`i18nextLng`). All user-facing text uses translation keys (`t("...")`).

Critical config (documented inline): `interpolation: { escapeValue: false }`. React already escapes text nodes at render time, so i18next's own HTML escaping is redundant and would corrupt interpolated values — e.g. a model name like `hf.co/unsloth/…` would have its `/` rendered as the literal `&#x2F;` in a toast. Disabling it stays XSS-safe because React performs the escaping.

---

## How the SPA is served (C0re static-files pattern)

In standalone dev the React app uses Vite's package-script port 5173; the Aspire AppHost overrides its single Vite endpoint to HTTPS 5175 and proxies to the Node Web Server; in production the built `dist/` is copied into the Node Web Server's `wwwroot/` at packaging time and served by ASP.NET Core. Source: `XE-Local-AI-Engine.Client/Program.cs`.

```
app.UseStaticFiles();                 // serve hashed assets from wwwroot
...
app.UseFastEndpoints(...);            // /api/local/v1/* (RoutePrefix = LocalApiRoutes.Prefix)
app.MapHub<LocalChatHub>(...);        // one of the unconditional Operator-authorized hubs
app.MapHub<SchedulerHub>(...);
app.MapHub<PreviewWorkflowHub>(...);
app.MapHub<BenchmarkRunHub>(...);
app.MapHub<DatasetGenerationHub>(...);
app.MapHub<TrainingRuntimeHub>(...);
app.MapHub<TrainingRunHub>(...);
app.MapHub<GgufDownloadHub>(...);
app.MapHub<CudaBuildHub>(...);
app.MapHub<LlamaCppSourceBuildHub>(...);
app.MapHub<RuntimeAcquisitionHub>(...);
app.MapHub<KnowledgeBaseHub>(...);
app.MapHub<ImageJobHub>(...);
app.MapHub<StableDiffusionCppSourceBuildHub>(...);
if (isDevelopmentModeEnabled)         // Development:Enabled, default true
    app.MapHub<DevelopmentAttemptHub>(...);
...
app.MapFallbackToFile("index.html");  // SPA fallback → client-side routing
```

Notes for maintainers:

- This repo uses `UseStaticFiles()` + `MapFallbackToFile("index.html")`. There is **no** `UseDefaultFiles()` call in `Program.cs` — the fallback to `index.html` is what makes deep links resolve to the SPA. (The C0re "static files" convention is the same shape; the `UseDefaultFiles` step is not present here.)
- Static files are mapped **before** the local-API security middleware; the API surface, auth and the hubs come after. Every unconditional hub and the conditional Development hub calls `RequireAuthorization(NodeAuthorizationPolicies.Operator)`. Full ordering and the per-hub contracts: [API & Hubs](09-api-and-hubs.md).
- Same-origin is the whole reason the axios `baseURL` is `""`: in production the SPA and the API share one origin, so relative paths just work.
- HTTPS redirect/HSTS are skipped in desktop mode (`isDesktop`); Swagger/Scalar (`/scalar`) is a development-only surface.

---

## Cross-cutting concerns a contributor must know

- **Generated code is off-limits to hand-edits.** Anything under `core/api/generated/` is regenerated; change the backend endpoint/DTO and run `pnpm openapi`, then commit the diff (`openapi:check` enforces this).
- **One axios instance, one baseURL.** Do not create ad-hoc axios instances or change `baseURL` away from `""`; both break the same-origin invariant. There is exactly **one** sanctioned exception — `authClient` in `core/auth/api/NodeAuthApi.ts` — and it exists so the shared instance's 401-refresh interceptor cannot recurse through the refresh call itself. Anything else belongs on the shared instance via `buildLocalApiUrl()`.
- **Hub connections are shared, not per-component.** Acquire through `SharedHubConnection`; never `new HubConnectionBuilder()` inside a feature hook.
- **Secrets never reach the store.** Auth/cloud/HMAC credentials stay node-local; the browser only ever holds a short-lived node access token in `NodeAuthStore`. See [Security & Privacy](12-security-and-privacy.md).
- **Feature isolation.** New UI goes in a `features/<name>/` folder mirroring the existing shape (`pages` / `components` / `queries` / `models` / optional `stores`); keep cross-feature reuse in `core/`. The one recorded exception is `workSessions`, whose centre pane is `features/chat`'s `Chat` component — see [Work Sessions](#work-sessions-the-chat-embed-seam) for why, and for the seven baselined `no-cross-feature` fingerprints that go with it.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [API & Hubs](09-api-and-hubs.md) — transport, OpenAPI/hey-api generation, SignalR hubs.
- [Chat](05-chat.md) — streaming chat UI + SignalR adapter.
- [Agent Mode](04-agent-mode.md) — agents, playbooks, skills, tools, preview canvas.
- [Scheduler](06-scheduler.md) — Quartz job management UI.
- [Model Fit](07-model-fit.md) — GGUF recommendation feature.
- [Local Runtime & Providers](03-local-runtime-and-providers.md) — models / loaded-models surfaces.
- [Hosting & Deployment](11-hosting-and-deployment.md) — wwwroot packaging, node settings.
- [Security & Privacy](12-security-and-privacy.md) — auth tokens, credential isolation.
- [Testing & Validation](13-testing-and-validation.md) — Vitest, lint/validate gates.
