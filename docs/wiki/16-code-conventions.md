# Code Organization Conventions

> Baseline: `9405df91e426c26b1942843dbd16a5edce1f466e` · Reviewed: 2026-08-08 · Code-grounded.
> Updated 2026-08-07: endpoint areas now fold DTOs/mappers/validators into `V1/{Dtos,Mappers,Validators}/`
> subfolders (only endpoints stay at the top level); `Dtos/` keeps a flat namespace by design.

This page states **where a new type or file goes and which house patterns to follow** — the conventions
that [02-project-layout.md](02-project-layout.md) (the *project* inventory and layering map) does not
cover. Every rule below was verified against the actual code on the baseline commit; each carries an
evidence citation. These are conventions, not always analyzer-enforced: the build can stay green while
they drift, so a reviewer (human or agent) has to know them.

> **Why this page exists.** These conventions are otherwise discoverable only by reading the code and
> inferring the pattern from a "gold-standard" example — and that inference is easy to get wrong. The
> `Services/Development/` folder was built the *opposite* way from `Services/AgentHome/` and the
> divergence went unnoticed for months because nothing enforced it. Writing the rules down lets drift be
> caught at PR time.
>
> **What is deliberately NOT here.** Rules already enforced by `.editorconfig` + analyzers (Release =
> warnings-as-errors) or by biome/tsc are not restated — documenting them is noise. That covers: C#
> interface `I`-prefix, `_camelCase` private fields, file-scoped namespaces, `Nullable=enable`, the
> `Async` suffix, one-`CancellationToken`-per-async-method; and TS path aliases (`@/…`), `import type`,
> string-literal-unions over `enum`, named `*Props` interfaces. Trust the linters for those.

---

## Backend (.NET)

### Endpoints: FastEndpoints, one per file — **not** MediatR/CQRS

The API layer is **FastEndpoints**. The dominant shape is **one endpoint class per `*Endpoint.cs` file**
under `Endpoints/{Area}/V1/`; a small number of areas group several into a plural `*Endpoints.cs`
(for example `NodeAuthEndpoints.cs`). Both are acceptable — **match the area you are editing**, don't
mass-convert either way. There is **no MediatR / `ISender` / CQRS vertical-slice layer** anywhere; an
endpoint injects and calls `Client.Application` services directly and stays **orchestration-only**
(no business logic, no persistence). It returns a **DTO record, never an EF entity**.
Canonical shape: `Endpoints/LocalChat/V1/DeleteNodeChatConversationEndpoint.cs` (sealed, primary-ctor
null-guards, `ct` + `ConfigureAwait(false)`, DTO return).

### A service's own model types live in `*ServiceModels.cs`

Gold standard: `Services/AgentHome/` in `Client.Application` — interfaces in `IAgentHome*.cs`, shared
records/exceptions in `AgentHomeServiceModels.cs`, concrete class under `Implementation/`. A service
(`*Service.cs`, `*Runner.cs`, `*Coordinator.cs`, `*Manager.cs`, `*Detector.cs`) should **not** inline its
own top-level input/result `record`s, `enum`s, or exceptions; move them to a sibling
`<ServiceName>Models.cs` in the same folder.

**Stays put:** a single small param/result record colocated with its only consumer; `private`/nested
records scoped inside a service; an interface file (`IXxx.cs`) carrying its own small contract records.

### Subfolders under `V1/` must nest their namespace — IDE0130 is a build error

**IDE0130** (namespace-must-match-folder) is `severity = error` in Release. Moving a file into a subfolder
therefore requires nesting its namespace to match: a file in `Endpoints/{Area}/V1/Mappers/` must be
`namespace …{Area}.V1.Mappers;`, and every consumer in the parent `…{Area}.V1` namespace must add a
`using …{Area}.V1.Mappers;` (a **child** namespace is *not* auto-visible to its parent). A **parent**
namespace *is* auto-visible to its children, so a mapper in `…V1.Mappers` sees the DTOs in `…V1` with no
`using`. Moving a type between files *in the same folder* stays a zero-risk pure move (no namespace/`using`
change). See `docs/agent-knowledge.md` for the full trap.

**The one deliberate exception — `Dtos/`.** DTO files live in `Endpoints/{Area}/V1/Dtos/` but keep the
**flat** `…{Area}.V1` namespace, and IDE0130 is disabled for `**/Endpoints/**/V1/Dtos/*.cs` in
`.editorconfig`. Reason: FastEndpoints/NSwag derives OpenAPI **schema IDs from the full type namespace**
(`XE_…EndpointsSkillsV1SkillResponse`); nesting a `.Dtos` segment would rename ~361 schema keys and churn
the entire generated hey-api client for no behavioural gain. Flat DTO namespaces keep the contract stable
*and* keep them same-namespace as the endpoints that consume them (no `using` needed). `Mappers/` and
`Validators/` are **not** serialized, so they nest normally (IDE0130 stays enforced there).

### `using` directives go **inside** the file-scoped namespace

Contrary to the near-universal C# convention: `.editorconfig` sets
`csharp_using_directive_placement = inside_namespace:warning`, and with warnings-as-errors that makes the
"normal" placement a **build error**. Every file is `namespace X;` first, then `using …;`
(the namespace/using order in `DeleteNodeChatConversationEndpoint.cs`). Do not "fix" it back to usings-on-top.

### An area folds into `V1/{Dtos,Mappers,Validators}/` — only endpoints stay at the top

Each `Endpoints/{Area}/V1/` folder keeps **only endpoint classes** (`*Endpoint.cs` / plural `*Endpoints.cs`)
at its top level. Supporting types are sorted into subfolders:

- **`Dtos/`** — request/response DTO files (`{Area}EndpointDtos.cs`, `{Area}Contracts.cs`). Flat namespace
  (see the IDE0130 exception above).
- **`Mappers/`** — the `internal static` endpoint↔DTO/entity mappers, one concern per file. Nested
  `…V1.Mappers` namespace; consumers add `using …V1.Mappers;`.
- **`Validators/`** — FastEndpoints `Validator<T>` classes. Nested `…V1.Validators` namespace.

Small endpoint-support helpers that are neither DTO, mapper, nor validator (a route-name constant, a wire
enum, a request-size-limit, a filename sanitizer) may **stay colocated at the top level** — they are
endpoint infrastructure, not a category worth a folder of one. An endpoint's own single request/response
record inlined in its `*Endpoint.cs` also stays put.

### DTO families still aggregate in one `*Dtos.cs` / `*Contracts.cs` — on purpose

Inside `Dtos/`, a file like `DevelopmentContracts.cs` (40+ records) deliberately holds a whole family of
related request/response records. Intentional — do **not** explode into one-record-per-file.

### Mappers are standalone files under `Mappers/`

Each `internal static` mapper is its own `{Name}Mapper.cs` in the area's `Mappers/` subfolder (e.g.
`DevelopmentContractMapper`, `CloudSettingsEndpointDtoMapper`, `NodeSettingsEndpointDtoMapper`,
`PreviewWorkflowResponseMapper`, `InvocationMonitorResponseMapper`, `SkillMapper`,
`TutorialStateMapper`). A mapper inlined inside a `*Dtos.cs`/`*Contracts.cs` or among endpoint classes in
an `*Endpoint(s).cs` file is the outlier — extract it into `Mappers/`.

### Persistence: EF Core + SQLite behind a `*Store` layer

`Client.Persistence` is EF Core + **SQLite** with per-column AEAD encryption (`UseSqlite` in
`NodeIdentityDbContextFactory.cs`), **not** Npgsql/PostgreSQL. Data access goes through a `*Store`
abstraction (`Client.Persistence/Stores` + `/Implementation`), not raw `DbContext` in services/endpoints.
Reads use `AsNoTracking`; set operations use `ExecuteUpdate/DeleteAsync`.

### DI + class house style

Constructor injection via **primary constructors**, with each dependency null-guarded
(`?? throw new ArgumentNullException(...)`) into a `readonly` field — this is the house style throughout
`Client.Application`, heavier than a plain primary ctor. Classes are `sealed` by default. Options bind from config via
the `*Options` pattern.

### Custom Tools keep authoring, offering, and execution gates aligned

Custom Tools follow the same endpoint/service/store separation as other areas, but their executable safety checks
are intentionally shared across boundaries: `CustomToolService` validates authoring input, `CustomToolCatalog` reads
live persisted definitions and applies the node switch + enabled + acknowledged + approval gates, and each
`ICustomToolExecutor` re-runs execution-time guards. Do not move SSRF or executable-path validation into the React form
alone, and do not treat the desktop executable probe as authorization to skip `HostExecutableGuard.Validate()` at
launch. Secret configuration belongs in encrypted `config_json`; DTOs expose only the mask sentinel.

### Providers depend only on `Providers.Abstractions`

The provider layering rule is in [02-project-layout.md](02-project-layout.md). A distinct, self-contained
collaborator (its own interface + result + class) that has accreted at the tail of a large provider
service belongs in its own file (same folder/namespace).

### Tests: TUnit, not xUnit

Backend tests are **TUnit** (`[Test]`) on Microsoft.Testing.Platform, with a project **`AssertEx`** helper
(`AssertEx.Equal/NotNull`) and **NSubstitute** for mocks — **no** xUnit/Shouldly/FluentAssertions/Moq.
Reach for a substitute only after the real thing and the repo's fake seam (`FakeOllama`,
`RecordingHubMessageSender`, MSW) have been ruled out, and never for the gate, cipher or migration the test exists
to verify — [17-writing-tests.md §1a](17-writing-tests.md#1a-test-principles).
Scope a run with `--treenode-filter` (not `--filter`). See
[13-testing-and-validation.md](13-testing-and-validation.md).

---

## Frontend (React / `XE-Local-AI-Engine.Client.React`)

### Feature-folder layout

`src/features/<x>/{pages, components, models, api, hooks, queries, stores}`. Placement rules:
- **Domain types, reducers, and action unions → `models/`.** A `FormState`/`FormAction` union, a
  `PartDraft` shape, or a stream-state type declared inline in a component belongs in the feature's
  `models/` folder. **Component-local `*Props` interfaces stay colocated** — idiomatic, not a violation.
- **No `mutations/` folder.** Despite the generic template, this repo has none — `useMutation` hooks are
  colocated in each feature's **`queries/`** folder alongside reads.
- **No API/data-fetch logic inside components** — it lives in `queries/`, `stores/`, or the generated
  client.

### Data layer = the hey-api generated client, not hand-written axios

The REST client is **generated by hey-api** from the backend OpenAPI into `src/core/api/generated/` and is
**read-only** (regenerate with `pnpm openapi:check`; never hand-edit). Consume it via the generated
`*Options()` / `*Mutation()` TanStack adapters from `@/core/api/generated/@tanstack/react-query.gen`
(e.g. `features/mcp/queries/useMcpServers.ts`). The generated options wire the **shared axios instance +
TanStack Query `AbortSignal` automatically** (`src/core/api/Generated.runtime.ts`) — do **not** thread
`signal` by hand or write bespoke request functions. Wrap each read/mutation in **`withResponseValidation(...)`**
so a Zod response-shape mismatch surfaces as an `ApiError`, never a raw `ZodError`.

### Forms are manual — no form library

There is **no `@tanstack/react-form` or `react-hook-form`** at runtime. Forms are controlled Mantine
inputs in local `useState`, validated by a **shared Zod schema on submit**, with `fieldErrors` state; a
dialog-hosted form exposes `submit()` via `useImperativeHandle` so the dialog footer button drives
validate-then-submit. Reference: `features/agents/components/AgentDefinitionForm.tsx`.

### State: server in TanStack Query, UI-only in Zustand

Server data lives in TanStack Query and is **never mirrored** into a store. Zustand stores hold only
ephemeral UI state, use a **nested `actions: { … }`** object (dominant convention across all
`features/**/stores/*.ts`), and are read with **one atomic selector per value**
(`useStore((s) => s.actions.x)`). `useShallow` is **deliberately unused** (0 occurrences) — avoid object
selectors rather than reaching for it. Reference: `features/mcp/stores/McpManagementStore.ts`.

### Auth / error interceptors live once on the shared axios instance

`Bearer` injection, 401→refresh, and the FormData Content-Type fix are registered once on the shared axios
instance (`src/core/api/axios/Interceptors.ts`) and therefore cover generated-SDK traffic too — not
per-call. User-facing toast/error strings route through `react-i18next` keys with an `apiErrorMessage`
fallback.

### `pnpm run lint` is the gate; `react-doctor` is advisory

The enforced gate is `pnpm run lint` (`tsc --noEmit` + `biome` + `stylelint` +
`CheckEventCurrentTargetInUpdaters`) and must be green. **`npx react-doctor` is advisory only** — its
score has no overlap with the gate; a low score with green lint means "review the findings," not "build
broken." Config is `doctor.config.jsonc` (JSONC for comments; a `.json` with `//` fails biome).

### Load-bearing suppressions — do not "fix" these

Some findings are intentional idioms carrying a justification comment; removing the suppression
reintroduces a real bug:
- **`no-ref-current-in-render`** in the SignalR hub hooks (`useSchedulerHub`,
  `useModelFitSchedulerEvents`, `useImageJobHub`, `usePreviewWorkflowHub`) — the *latest-value ref* idiom;
  making these effect deps would tear down and rebuild the hub connection mid-negotiation.
- **`effect-needs-cleanup`** on those hooks — the cleanup is real but hidden behind a shared refcount
  (`hub.release()` + `connection.off(...)`); the rule can't see the indirection.
- **`async-await-in-loop`** in the chat SignalR adapters (`NodeChatAdapter`, `NodeChatConnection`,
  `NodeChatStreamGuard`) — wire-order sequential awaits; parallelizing would race one connection.

### God-component decomposition is a reviewed pass, not a drive-by

Large orchestration components (`chat/pages/Chat.tsx` and peers) carry `no-giant-component` suppressions
with justifications. Decomposing them changes render structure and is regression-prone — do it as its own
reviewed change with lint + test + build run, preferably starting from a component whose sub-parts already
exist in-file (a mechanical extract, as done for `ImageModelManager.tsx`).

---

## Relationship to the generic `.opencode` standards

A local, **git-ignored** context tree at `.opencode/context/core/standards/` (`code.md`, `csharp.md`,
`csharp-project-structure.md`, `react.md`, `typescript.md`) carries the OpenSystemBuilder house standards.
They are **generic templates**, not part of the versioned tree — a fresh clone does not have them. This
page is the **repo-true, committed authority**, distilled by verifying each template rule against the
actual code; where the two disagree, this page and the code win.

**Verified DIVERGENCES — do NOT import the template's version into this codebase:**

| Generic template says | This repo actually does (verified) |
|---|---|
| Minimal APIs + **MediatR/CQRS** vertical slices (`Features/`, `ISender`, `IRequestHandler`) | **FastEndpoints**, one `*Endpoint.cs` per endpoint, calling `Client.Application` services directly — zero MediatR |
| `using` directives **above** the namespace | `using` **inside** the file-scoped namespace (`.editorconfig` → build error otherwise) |
| **PostgreSQL** + Npgsql, raw `DbContext` in handlers | **SQLite** + per-column AEAD, access via a `*Store` layer |
| **xUnit + Shouldly + Moq** | **TUnit** `[Test]` + `AssertEx` + **NSubstitute** |
| Command/Query record *is* the contract, no DTO/mapping layer | explicit `*Dtos.cs`/`*Contracts.cs` in `V1/Dtos/` + standalone mappers in `V1/Mappers/` |
| `GlobalUsings.cs` / `global using` | none — relies on `ImplicitUsings=enable` + explicit per-file usings |
| `Result<T>` return pattern | not used — FastEndpoints `Send.NotFoundAsync` + nullable returns |
| Hand-written `Api/` + axios interceptor request functions | **hey-api** generated client is the single REST source; TanStack Query wraps it |
| `@tanstack/react-form` + `zodValidator` | **manual** forms (Mantine + `useState` + Zod-on-submit) |
| Zustand object selectors + `useShallow`; `mutations/` folder | atomic selectors (no `useShallow`); mutations in `queries/` |

**Universal `code.md` rules that DO hold here** (and this page reflects): endpoints stay
orchestration-only; match the nearest existing subsystem's folder shape before inventing one; the C#/TS
naming families; never return persistence entities across a transport boundary; generated artifacts
(`src/routeTree.gen.ts`, `src/core/api/generated/**`, EF migrations) are read-only — regenerate, never
hand-edit.

---

## See also

- [02-project-layout.md](02-project-layout.md) — project inventory, dependency graph, layering rule.
- [09-api-and-hubs.md](09-api-and-hubs.md) — FastEndpoints route families, hubs, OpenAPI→hey-api.
- [10-react-client.md](10-react-client.md) — React client architecture.
- [13-testing-and-validation.md](13-testing-and-validation.md) — the gates these conventions ride on.
- `docs/agent-knowledge.md` — the hard-won traps (IDE0130, Release-only analyzers, bare-`TODO` build break).
- `.editorconfig`, `Directory.Build.props`, `doctor.config.jsonc` — where the auto-enforced rules live.
