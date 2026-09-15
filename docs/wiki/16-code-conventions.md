# Code Organization Conventions

> Reviewed: 2026-09-15 · Code-grounded.
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
> `Async` suffix, one-`CancellationToken`-per-async-method; and, on the TS side, `import type` placement,
> import ordering, and string-literal-unions over `enum`. Trust the linters for those. The `@/` alias and the
> `*Props` interface shape are **not** linter-enforced — they are conventions, and are stated under Frontend below.

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
`GraphWorkflowContractMapper`, `InvocationMonitorResponseMapper`, `SkillMapper`,
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

### Placement is pinned by architecture tests, not by review

IDE0130 checks that a file's namespace matches the folder the file **already** sits in; it cannot tell you the
file is in the wrong folder. Three rules in `XE-Local-AI-Engine.Tests/Architecture/` close that gap and fail the
build's test gate, not the reviewer's memory:

| Convention | Enforced by |
| --- | --- |
| A public **interface** in a `Providers.*` project lives in `…Providers.<Name>.Contracts`. `Providers.Abstractions` is out of scope — it *is* the contracts layer. | `PlacementConventionTests.ProviderPublicInterfaces_ResideInTheProviderContractsNamespace` |
| A public **`*Options`** class in a `Providers.*` project lives in `…Providers.<Name>.Options`. | `PlacementConventionTests.ProviderOptionsClasses_ResideInTheProviderOptionsNamespace` |
| In the host: a FastEndpoints endpoint lives in a `.V1` namespace, a `*Mapper` in `.V1.Mappers`, an `IValidator` in `.V1.Validators`. | `PlacementConventionTests.ClientEndpointsMappersAndValidators_ResideInTheirVersionedNamespaces` |
| A service implementation file under `Client.Application/Services/{AgentHome,Benchmarks,Development,Integrations,Training,WorkSessions}/` declares the service and nothing else — its records and enums go in a sibling `*Models` / `*ServiceModels` / `*Contracts` / `*Dtos` file. | `ServiceModelColocationTests.ServiceImplementationFiles_DoNotAlsoDeclareContractTypes` |

The first three are **ArchUnitNET** (`TngTech.ArchUnitNET`, test-project only) over compiled IL; it sits
alongside NetArchTest, which pins dependency *direction* between assemblies rather than placement inside one.
The fourth is a source-text scan, because IL records no source file and file co-location is the whole point of
that rule; the `Services/` folders outside that list still hold pre-existing co-located declarations and are
deliberately out of scope.

Concrete implementations are **not** required to sit under `Implementation/`: providers legitimately keep
root-level DTOs, enums, exceptions and value records (`LlamaBinary`, `GpuVariant`, `LlamaRuntimeException`, …),
and no rule forces those to move.

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

### Components and hooks are named function declarations

**A component is `export function ComponentName(props: ComponentNameProps)`** — a named function
declaration with a named export. Not an arrow-function const, not `React.FC<…>` (zero occurrences in the
tree), not a default export. The two `export default function` components are strays, not a second school.

Two sanctioned exceptions:

- **`export const Foo = memo(function Foo(props: FooProps) { … })`** for a component that needs render-skip
  memoization — keep the inner function named so stack traces and Devtools still say `Foo`. Reference:
  `features/chat/components/MessageParts.tsx`, `core/ui/components/CodeBlock/CodeBlock.tsx`.
- **A Zustand store hook stays `export const useFooStore = create<T>()(…)`** — it is a factory invocation,
  not a declaration; do not "fix" one back to `function`. Every other hook is `export function useFoo()`
  (121 files against one stray const hook, `core/theme/hooks/useTheme.ts`).

### Props are an `interface <Component>Props`, colocated

**The props type is an `interface` named after the component, declared in the component's own file** — 204
files do this and `type FooProps = …` appears zero times, so do not introduce it. A bare `interface Props`
is drift: name it after the component.

Export the interface only when another file imports it. **Open decision:** 72 of the 204 are exported and
nothing mechanical separates a genuinely shared one from a leftover, so this page states no rule on pruning
them. Likewise `Readonly<Props>` is a minority stricter-typing choice (18 of 204) with no majority behind
it — neither adding nor removing it is a violation today.

### Feature-folder layout

`src/features/<x>/{pages, components, models, api, hooks, queries, stores}`. Placement rules:
- **Domain types, reducers, and action unions → `models/`.** A `FormState`/`FormAction` union, a
  `PartDraft` shape, or a stream-state type declared inline in a component belongs in the feature's
  `models/` folder. **Component-local `*Props` interfaces stay colocated** — idiomatic, not a violation.
- **No `mutations/` folder.** Despite the generic template, this repo has none — `useMutation` hooks are
  colocated in each feature's **`queries/`** folder alongside reads.
- **No API/data-fetch logic inside components** — it lives in `queries/`, `stores/`, or the generated
  client.
- **A feature's `components/` folder is flat: `components/Foo.tsx`** (289 flat against 2 nested). A
  `components/<Parent>/` folder exists only to hold the sub-parts of a decomposed parent component, not as
  the default shape for a single component.
- **A shared `core/` component gets its own folder: `core/**/components/<Name>/<Name>.tsx`**, with its test
  and any `.module.css` beside it (28 nested, zero flat). Reference:
  `core/ui/components/FullHeightPage/FullHeightPage.tsx`. The two halves of the tree follow different rules
  on purpose — match the half you are editing.
- **No `index.ts` barrels** anywhere under `src/features` — import the file directly.
- **There is no `types/`, `utils/`, or `constants/` folder** in any feature, and adding one starts a fourth
  category. A domain type goes in `models/` (present in 33 of 35 features), a helper sits beside its only
  caller or in `core/` when shared. `api/` exists in four features only.

### Imports use the `@/` alias; their order is Biome's output

**Import across folders through the `@/` alias, never a `../../` climb** — 3895 alias occurrences against
zero two-level relative imports. A single `../` to a sibling inside the same feature is fine.

**Import order and `import type` placement are `biome check --write` output, not a style choice.**
`organizeImports` is on with explicit `:PACKAGE:`/`:ALIAS:`/`:PATH:` groups and `useImportType` is an error,
so Biome decides top-level `import type { … }` versus inline `{ type X }` per import. Do not hand-tune
either; a reordering diff means the file was not formatted.

### Data layer = the hey-api generated client, not hand-written axios

The REST client is **generated by hey-api** from the backend OpenAPI into `src/core/api/generated/` and is
**read-only** (regenerate with `pnpm openapi:check`; never hand-edit). Consume it via the generated
`*Options()` / `*Mutation()` TanStack adapters from `@/core/api/generated/@tanstack/react-query.gen`
(e.g. `features/mcp/queries/useMcpServers.ts`). The generated options wire the **shared axios instance +
TanStack Query `AbortSignal` automatically** (`src/core/api/Generated.runtime.ts`) — do **not** thread
`signal` by hand or write bespoke request functions. Wrap each read/mutation in **`withResponseValidation(...)`**
so a Zod response-shape mismatch surfaces as an `ApiError`, never a raw `ZodError`.

### Forms are manual — no form library

There is **no schema-bound form library** in the dependency set. Forms are controlled Mantine
inputs in local `useState`, validated by a **shared Zod schema on submit**, with `fieldErrors` state; a
dialog-hosted form exposes `submit()` via `useImperativeHandle` so the dialog footer button drives
validate-then-submit. Reference: `features/agents/components/AgentDefinitionForm.tsx`.

### State: server in TanStack Query, UI-only in Zustand

Server data lives in TanStack Query and is **never mirrored** into a store. Zustand stores hold only
ephemeral UI state, use a **nested `actions: { … }`** object (16 of the 22 Zustand stores, and the shape to
write for a new store anywhere), and are read with **one atomic selector per value**
(`useStore((s) => s.actions.x)`). `useShallow` is **deliberately unused** (0 occurrences) — avoid object
selectors rather than reaching for it. Reference: `features/mcp/stores/McpManagementStore.ts`.

Six stores predate the nested shape and expose flat top-level action fields — `core/layout/stores/SidebarStore`,
`core/locales/stores/UserLanguageStore`, `core/theme/stores/ThemeStore`,
`core/ui/components/TablePagination/useTablePaginationStore`, `features/model-fit/stores/CpuFallbackBannerStore`,
`features/node-settings/stores/RuntimeUpdateBannerStore`. **Open decision:** converting them is an API change at
every call site and nothing has decided it is worth doing, so they are a documented exception, not a bug.

### An unsaved-changes guard on a search-param page needs `allowSameRoute`

`useUnsavedChangesGuard({ isDirty })` blocks every navigation, including one that only rewrites a search
param. On a page whose selections live in search params — the [Graph Workflows](21-graph-workflows.md)
editor is the one that hit this — that turns clicking a node into a leave prompt. Pass `allowSameRoute: true`
there: it compares pathnames and blocks a real route change while letting a same-route search-param write
through. The default is unchanged, and the other seven callers do not set it.

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
  `useModelFitSchedulerEvents`, `useImageJobHub`) — the *latest-value ref* idiom;
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

### Styling: Mantine props first, `.module.css` next, inline `style` last

**Reach for Mantine's style props (`mt`, `p`, `gap`, `w`, …) first** — they are the dominant mechanism by an
order of magnitude. Escalate only when they cannot express the thing:

1. **`.module.css`** for pseudo-selectors, keyframes, and React Flow node styling — the handful of modules in
   the tree are almost all canvas nodes in `devWorkflows`/`graphWorkflows`.
2. **Inline `style={{ … }}` only for a genuinely computed value** (a measured offset, a progress width). A
   static inline style object is a Mantine prop that has not been written yet, and 319 such sites across 24 of
   35 features make this the app's largest style drift.

**UnoCSS utility classNames are not this app's convention.** They belong to the standalone
`src/modules/theme-configurator/` and to the app shell's static layout classes
(`core/layout/components/Layout/Layout.tsx`). Do not introduce them in a feature.

### Responsive layout: breakpoints come from `LayoutBreakpoints.ts`

**Never write a breakpoint literal.** Every cutoff lives in `core/layout/constants/LayoutBreakpoints.ts`
(`COMPACT_CONTROLS_BREAKPOINT`, `DESKTOP_NAV_BREAKPOINT`, `TWO_PANE_BREAKPOINT`), and its sibling test pins
them to the Mantine theme so a theme change fails loudly instead of drifting the shell apart.

Three mechanisms cover the app; copy the one that matches what has to change:

- **`SimpleGrid cols={{ base, sm, lg }}`** when a card or list grid only needs to reflow. Reference:
  `features/dashboard/pages/Dashboard.tsx`.
- **`useWindowDimensions` compared against a named `LayoutBreakpoints` constant** when the layout swaps a
  whole subtree (a pane becomes a Drawer) and the VIEWPORT really is the space in question. Reference:
  `core/layout/components/Layout/Layout.tsx`. Mantine's `visibleFrom`/`hiddenFrom` is used in one file
  (`features/node-settings/components/NodeSettingsUsageRatesCard.tsx`, three call sites), for a pure show/hide
  at `sm`: fine for CSS-only visibility, not the pattern for swapping a subtree. `useMediaQuery` is not used at
  all — it reads asynchronously and flashed the wrong layout on first paint.
- **`usePaneLayoutMode` (`core/ui/components/ResponsivePaneLayout/`)** for the three-pane pages. It measures
  the page's own container with a `ResizeObserver` instead of the viewport, because the panes sit inside the
  app shell: the sidebar and the content padding take ~250px first, and the sidebar collapses with no window
  resize at all. The page makes the decision ONCE for the surfaces the PAGE owns and hands the same boolean to
  its Drawers, its header toggles and `ResponsivePaneLayout`; the viewport constant survives only as the
  fallback while the container is still unmeasured. The threshold is derived from the grid's own track sizes,
  so it cannot drift from them. That decision does not reach inside a pane, though: a pane that is responsive
  in its own right still reads the viewport for itself — the chat embedded in the work-session grid does
  exactly that, in `features/chat/components/ChatDisplayShell.tsx` (its message list, against
  `TWO_PANE_BREAKPOINT`) and `ChatInputArea.tsx` (its composer's context-usage readout).

**Wide content owns its own horizontal scroller.** The shell clips both axes (`Layout`) and `FullHeightPage`
deliberately clips X, so a table or diagram wider than its pane must carry its own `Table.ScrollContainer` or
`ScrollArea` — otherwise it is silently cut off, not scrollable. Reference:
`features/mcp/components/McpServerList.tsx`.

### Tests: colocated, `renderWithProviders`, role queries in new tests

**Test files are colocated `*.test.tsx` beside the file under test and use `describe`/`it`** — zero
`__tests__/` folders, zero bare `test()`.

**A component test needing Mantine theme or TanStack Query context uses `renderWithProviders` from
`src/test/RenderWithProviders.tsx`.** A hand-rolled `MantineProvider` + `QueryClientProvider` wrapper
duplicates it; the 121 test files that still do are the drift, not the pattern.

**In a new or touched test, prefer `getByRole`/`findByRole` where the element has a semantic role** and keep
`getByTestId` for elements that genuinely have none. This is forward-looking only: existing `*ByTestId` sites
outnumber role queries roughly twelve to one and **are not to be rewritten in bulk** — nothing lints this, and
a loose role match fails a test silently.

### No user-facing string is a literal

**Every user-facing string — text node and attribute alike — goes through `useTranslation()` / `t()`.**
Attribute strings (`label`, `placeholder`, `title`, `aria-label`) are already free of literals; JSX text nodes
are the remaining gap. Module-scope `i18next.t()` is reserved for non-component formatters that cannot call a
hook (`core/formatting/TimeFormatting.ts`).

`<Trans>` is unused here. A string with embedded markup is currently split into separate keys — introducing
`<Trans>` as a third pattern is a team decision, not a drive-by.

---

## See also

- [02-project-layout.md](02-project-layout.md) — project inventory, dependency graph, layering rule.
- [09-api-and-hubs.md](09-api-and-hubs.md) — FastEndpoints route families, hubs, OpenAPI→hey-api.
- [10-react-client.md](10-react-client.md) — React client architecture.
- [13-testing-and-validation.md](13-testing-and-validation.md) — the gates these conventions ride on.
- `docs/agent-knowledge.md` — the hard-won traps (IDE0130, Release-only analyzers, bare-`TODO` build break).
- `.editorconfig`, `Directory.Build.props`, `doctor.config.jsonc` — where the auto-enforced rules live.
