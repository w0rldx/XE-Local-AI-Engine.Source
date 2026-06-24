# React Client (Frontend)

> Last reviewed: 2026-06-24 · Code-grounded.

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
| Realtime | `@microsoft/signalr` 10 | Chat, scheduler, preview hubs. |
| Markdown | `react-markdown` + `remark-gfm` + `react-syntax-highlighter` | Chat + editor rendering. |
| i18n | `i18next` + `react-i18next` + browser language detector | `en` / `de`. |
| Canvas | `@xyflow/react` | Preview workflow builder (see [Agent Mode](04-agent-mode.md) / Preview). |

Tooling gates (`pnpm build` / `pnpm lint`): `tsc --noEmit`, a custom `scripts/CheckEventCurrentTargetInUpdaters.mjs` guard, Biome lint, Stylelint, plus `knip` and `dependency-cruiser` in `pnpm validate`.

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

i18n is initialized as a side-effect import (`src/i18n.ts`) and `dayjs` is extended with the UTC plugin at module load.

---

## Directory layout

Two top-level trees under `src/`:

- **`src/core/`** — cross-cutting infrastructure: API/transport (`core/api`), auth (`core/auth`), routing/query integrations (`core/integrations`), layout/navigation (`core/layout`), theme (`core/theme`), locales plumbing (`core/locales`), dev tools (`core/dev-tools`), and the shared UI library (`core/ui`).
- **`src/features/`** — one folder per product area, each self-contained with `pages/`, `components/`, `queries/` (TanStack Query hooks), `models/` (DTO ↔ view mappers + Zod), and optionally `stores/`.

### The 17 feature folders

Source: `ls XE-Local-AI-Engine.Client.React/src/features`.

| Feature | What it owns | Deep-dive |
|---|---|---|
| `about` | About dialog + build/version info | — |
| `agents` | Agent definitions, templates, playbooks, golden conversations, feedback insights, execution logs, orchestration topology | [Agent Mode](04-agent-mode.md) |
| `api-foundation` | Validation-problem probe (boundary/error-contract harness) | [API & Hubs](09-api-and-hubs.md) |
| `binding` | Node binding to the C0re platform | [Security & Privacy](12-security-and-privacy.md) |
| `chat` | Streaming chat UI, SignalR adapter, reasoning/tool-call rendering, sampling options | [Chat](05-chat.md) |
| `cloud-settings` | Cloud provider credentials/config (kept node-local) | [Security & Privacy](12-security-and-privacy.md) |
| `dashboard` | Node overview surface | — |
| `invocations` | Tool/function invocation history | [Agent Mode](04-agent-mode.md) |
| `loaded-models` | Live loaded-model overview + graceful eject | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `mcp` | MCP server registration + tooling | [API & Hubs](09-api-and-hubs.md) |
| `model-fit` | Box-aware GGUF recommendation + benchmark | [Model Fit](07-model-fit.md) |
| `models` | Model management (HF GGUF discovery/download, classification) | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `node-settings` | User-editable cached node settings, local runtime config | [Hosting & Deployment](11-hosting-and-deployment.md) |
| `preview` | Open Canvas (Preview) workflow builder (React Flow) | [Agent Mode](04-agent-mode.md) |
| `scheduler` | Quartz job management + run history | [Scheduler](06-scheduler.md) |
| `skills` | Node skill library + per-agent skill picklist | [Agent Mode](04-agent-mode.md) |
| `tools` | Tool catalog / capability surface | [Agent Mode](04-agent-mode.md) |

Each feature follows the same shape, e.g. `features/agents/` has `pages/AgentsPage.tsx`, `components/*` (forms, panels, gallery), `queries/use*.ts` (TanStack Query hooks), `models/*Models.ts` + `*Mappers.ts` (Zod + DTO mapping), and `stores/AgentManagementStore.ts`.

---

## State strategy

Two stores, strictly separated:

1. **Server state → TanStack Query.** Every REST read/write is a query/mutation hook under a feature's `queries/` folder (e.g. `features/agents/queries/usePlaybookActions.ts`, `features/skills/queries/useSkills.ts`). The `QueryClient` is provided by `core/integrations/tanstack-query/Provider.tsx`. Server data is **not** mirrored into Zustand.
2. **UI/session state → Zustand.** ~19 stores (`find src -name "*Store.ts*"`), e.g. `core/auth/stores/NodeAuthStore.tsx` (access token + actions), `core/theme/stores/ThemeStore.tsx`, `core/layout/stores/SidebarStore.tsx`, `core/locales/stores/UserLanguageStore.tsx`, `core/dev-tools/stores/DeveloperModeStore.ts`, and `core/ui/components/TablePagination/useTablePaginationStore.ts`.

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

- `types.gen.ts` (~2316 symbols) — request/response DTO types.
- `sdk.gen.ts` (~132 symbols) — typed operation functions.
- `@tanstack/react-query.gen.ts` (~178 symbols) — query/mutation option factories.
- `zod.gen.ts` (~434 symbols) — Zod schemas mirroring the DTOs.
- `client/`, `core/` — hey-api runtime (axios adapter, serializers, SSE).

`pnpm openapi:check` fails if the generated output drifts from the committed OpenAPI doc — the backend OpenAPI document is the single source of truth.

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

In dev the React app runs under Vite (5173) and proxies to the Node Web Server; in production the built `dist/` is copied into the Node Web Server's `wwwroot/` at packaging time and served by ASP.NET Core. Source: `XE-Local-AI-Engine.Client/Program.cs`.

```
app.UseStaticFiles();                 // serve hashed assets from wwwroot
...
app.UseFastEndpoints(...);            // /api/local/v1/* (RoutePrefix = LocalApiRoutes.Prefix)
app.MapHub<LocalChatHub>(...);        // local SignalR hubs (Operator-authorized)
app.MapHub<SchedulerHub>(...);
app.MapHub<PreviewWorkflowHub>(...);
...
app.MapFallbackToFile("index.html");  // SPA fallback → client-side routing
```

Notes for maintainers:

- This repo uses `UseStaticFiles()` + `MapFallbackToFile("index.html")`. There is **no** `UseDefaultFiles()` call in `Program.cs` — the fallback to `index.html` is what makes deep links resolve to the SPA. (The C0re "static files" convention is the same shape; the `UseDefaultFiles` step is not present here.)
- Static files and the fallback are mapped **after** the local-API security middleware (`LocalApiSecurityMiddleware`) and auth; the three SignalR hubs each `RequireAuthorization(NodeAuthorizationPolicies.Operator)`.
- Same-origin is the whole reason the axios `baseURL` is `""`: in production the SPA and the API share one origin, so relative paths just work.
- HTTPS redirect/HSTS are skipped in desktop mode (`isDesktop`); Swagger/Scalar (`/scalar`) and the Agent Framework DevUI (`/devui`) are dev-only and never shipped to production.

---

## Cross-cutting concerns a contributor must know

- **Generated code is off-limits to hand-edits.** Anything under `core/api/generated/` is regenerated; change the backend endpoint/DTO and run `pnpm openapi`, then commit the diff (`openapi:check` enforces this).
- **One axios instance, one baseURL.** Do not create ad-hoc axios instances or change `baseURL` away from `""`; both break the same-origin invariant.
- **Secrets never reach the store.** Auth/cloud/HMAC credentials stay node-local; the browser only ever holds a short-lived node access token in `NodeAuthStore`. See [Security & Privacy](12-security-and-privacy.md).
- **Feature isolation.** New UI goes in a `features/<name>/` folder mirroring the existing shape (`pages` / `components` / `queries` / `models` / optional `stores`); keep cross-feature reuse in `core/`.

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
