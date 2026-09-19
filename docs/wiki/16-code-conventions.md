# Code Organization Conventions

> Reviewed: 2026-09-17 · Code-grounded.
> Updated 2026-08-07: endpoint areas now fold DTOs/mappers/validators into `V1/{Dtos,Mappers,Validators}/`
> subfolders (only endpoints stay at the top level); `Dtos/` keeps a flat namespace by design.
> Updated 2026-09-17: the backend sections below state the **C# baseline** — conventional constructors,
> `sealed class` DTOs, contextual `ConfigureAwait`, `*Store`-only data access, comment budgets. The baseline
> is the rule for code you write or touch **now**; the migration of existing code runs as a slice series, and
> each rule names the slice that carries it. **Existing code that still shows the old shape is migration debt,
> not a pattern to copy** — and equally, it is not to be "repaired" back to the old shape.

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
(no business logic, no persistence). It returns a **DTO, never an EF entity** — and a DTO is a `sealed class`
with `required`/`init` members (see [DTOs, records and type choice](#dtos-records-and-type-choice)).

Canonical shape: sealed, one endpoint per `*Endpoint.cs`, route derived from `LocalApiRoutes`, a conventional
constructor guarding its dependencies into `private readonly` fields, the request's `CancellationToken` threaded
through every call, no `ConfigureAwait` (the host is not a library — see
[ConfigureAwait is contextual](#configureawait-is-contextual)), DTO return.
`Endpoints/LocalChat/V1/DeleteNodeChatConversationEndpoint.cs` is the structural reference, its constructor
included.

Both halves of that shape are now regression guards rather than review habits.
`EndpointConventionTests` freezes the file and type conventions: one endpoint per `*Endpoint.cs` file named
after the type it declares, the plural `*Endpoints.cs` groupings named in an allowlist and nothing else plural,
every endpoint `sealed`, and every route derived from `LocalApiRoutes` rather than written as a string literal.
`EndpointDependencyTests` enforces the dependency rule above the same way, for every endpoint in the host and with
no exemption list. Generic arguments are walked recursively, so a forbidden type wrapped in an allowed generic
counts too. The rule now covers the **whole host**, not only its request edge: every SignalR hub, every
DI-constructed class under `Endpoints/`, and every other class in the host assembly that DI builds — hosted
services, hub-side event publishers, exception handlers, authentication handlers, boot backfills. The composition
root is outside it by construction rather than by exemption (`Program`, `ConfigureServices` and the
`Add*Extensions` classes are static or parameterless, and naming a concrete implementation is what composing the
object graph *is*), and so is the Data Protection key ring, whose decryptor type name is persisted inside every
encrypted key-ring element and therefore cannot move. `HostServiceResolutionTests` closes the other half of the
same rule with a source scan: a host class may not pull a forbidden type out of the container with
`GetRequiredService<T>` either, which is how three retention sweepers held persistence stores that no constructor
scan could see. A host type that genuinely cannot move down — the model proxy's forwarder owns an `HttpContext`,
first-run provisioning gates on the process's own launch mode — takes what it needs through an application-layer
service instead, which is what `LlamaCppRuntimeOrchestrationService` is. The test once carried a shrink-only allowlist keyed by
fully-qualified endpoint-and-parameter pairs,
frozen at the persistence-store and concrete-provider injections that existed when the rule was written; slices
S6a–S6m migrated those sites area by area until it was empty and then deleted it. Nothing may be added back: a
violation is fixed by moving the dependency into a `Client.Application` service the endpoint injects instead. Such
a service is a concrete `sealed class` with no interface, registered as itself; add an interface only when a
specific test must substitute it, and move the displaced behavioural assertions to a service-level test when you
do. The last such site was the tool-catalog endpoint's `AI.Agent` approval policy, which went behind
`ToolCatalogService`; `AI.Agent` is deliberately not in the rule's allowed set.

### A service's own model types live in `*ServiceModels.cs`

Gold standard: `Services/AgentHome/` in `Client.Application` — interfaces in `IAgentHome*.cs`, shared
models/exceptions in `AgentHomeServiceModels.cs`, concrete class under `Implementation/`. A service
(`*Service.cs`, `*Runner.cs`, `*Coordinator.cs`, `*Manager.cs`, `*Detector.cs`) should **not** inline its
own top-level input/result types, `enum`s, or exceptions; move them to a sibling
`<ServiceName>Models.cs` in the same folder. Those models are `sealed class`es — see
[DTOs, records and type choice](#dtos-records-and-type-choice).

**Stays put:** `private`/nested types scoped inside a service. A top-level param/result type sharing a file with
its only consumer is migration debt under the one-type-per-file rule below, not an exemption.

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
endpoint infrastructure, not a category worth a folder of one. An endpoint's **own** request/response DTO does
**not**: it is a DTO like any other and belongs in `Dtos/`.

### One top-level type per file — the family files are the exception

A `.cs` file declares one top-level type, named after the file. The documented exceptions are the DTO/contract
and service-model **family files** below (`*Dtos.cs`, `*Contracts.cs`, `*ServiceModels.cs`/`*Models.cs`), which
group a related family on purpose. Everything else is drift in one of two directions: a DTO inlined in an
`*Endpoint.cs`, or a stray type riding along in a `*Service.cs`. Both move out — the DTO to the area's `Dtos/`
family file (the namespace stays flat, so this is a pure move), the stray type to the service's sibling
`*ServiceModels.cs`.

*Migration status:* slice **S7f** moves the DTOs still inline in endpoint files and triages the remaining
multi-type files; `ServiceModelColocationTests` already fails the gate for the `Services/` folders it covers.

### DTO families still aggregate in one `*Dtos.cs` / `*Contracts.cs` — on purpose

Inside `Dtos/`, a file like `DevelopmentContracts.cs` (40+ types) deliberately holds a whole family of
related request/response DTOs. Intentional — do **not** explode into one-type-per-file.

### Mappers are standalone files under `Mappers/`

Each `internal static` mapper is its own `{Name}Mapper.cs` in the area's `Mappers/` subfolder (e.g.
`DevelopmentContractMapper`, `CloudSettingsEndpointDtoMapper`, `NodeSettingsEndpointDtoMapper`,
`GraphWorkflowContractMapper`, `InvocationMonitorResponseMapper`, `SkillMapper`,
`TutorialStateMapper`). A mapper inlined inside a `*Dtos.cs`/`*Contracts.cs` or among endpoint classes in
an `*Endpoint(s).cs` file is the outlier — extract it into `Mappers/`.

### Persistence: EF Core + SQLite behind a `*Store` layer

`Client.Persistence` is EF Core + **SQLite** with per-column AEAD encryption (`UseSqlite` in
`NodeIdentityDbContextFactory.cs`), **not** Npgsql/PostgreSQL. Reads use `AsNoTracking`; set operations use
`ExecuteUpdate/DeleteAsync`.

### The repository abstraction is `*Store` — an Application service never holds a `DbContext`

There is no `IRepository` in this repo and none is to be introduced: the abstraction over persisted state is a
`*Store` (`Client.Persistence/Stores` + `/Implementation`). A service or endpoint that needs data injects a store;
EF and raw SQL stay behind it, in `Client.Persistence`. A query the stores cannot express is a missing store
method, not a reason to inject `NodeChatDbContext`/`NodeIdentityDbContext` into `Client.Application`.

A `DbContext` outside `Client.Persistence` is legitimate only where the code creates or owns its scope for
infrastructure reasons rather than doing domain work: the **composition root** (DI registration, ASP.NET
Identity's `AddEntityFrameworkStores<T>` generic argument), the **health check**, the **migration bootstrap** and
the **retention sweeper**. Every other holder is migration debt.

*Migration status:* slice **S7** fences this with a shrink-only architecture test whose allowlist is the
authoritative list of remaining holders, then migrates them behind stores. The database-maintenance services
under `Services/Persistence/` (backup, migration recovery, encryption backfill) are decided there case by case:
move into `Client.Persistence`, or join the legitimate set.

### DI + class house style

**Conventional constructors — no primary constructors on classes or structs.** Each dependency is assigned to a
`private readonly` field, and a dependency that is guarded is guarded with `ArgumentNullException.ThrowIfNull(x)`,
never `?? throw new ArgumentNullException(...)`; converting a primary constructor adds no guard that was not
already there, because a guard that did not exist is a behaviour change. This is a project convention, not a judgement about the language
feature: a uniform shape keeps the guard, the field and the injected name in one readable block, and keeps
constructor bodies (validation, derived state) available without a later rewrite from primary-ctor form.

A class whose constructor takes **many** dependencies is a responsibility smell. Converting it to a conventional
constructor makes that visible — which is the point; **report it, do not hide it** behind a terser syntax.
Decomposing such a class is its own reviewed change, never a drive-by inside a mechanical pass.

Classes are `sealed` by default. Options bind from config via the `*Options` pattern. Never read ambient time
(`DateTimeOffset.UtcNow`/`.Now`, `DateTime.*`) directly; inject `TimeProvider` (registered once in
`Client/ConfigureServices.cs`, no `?? TimeProvider.System` defaults) and call `GetUtcNow()`/`GetLocalNow()` —
enforced by `BannedSymbols.txt` (RS0030), documented in [Security & Privacy](12-security-and-privacy.md) §8.

`.editorconfig` sets `csharp_style_prefer_primary_constructors = false`, but IDE0290 does not flag a primary
constructor that already exists, so a source scan enforces the rule: `PrimaryConstructorConventionTests` fails on
any class or struct declaration that carries a parameter list (records are out of scope). It derives its coverage
from the solution and fences every project in it, plus the C# under `tools/`. There is no exemption list.

### DTOs, records and type choice

**Default to a `sealed class` with `required`/`init` members.** That covers API request/response DTOs, SignalR hub
payloads, `Client.Application` service models, `Client.Persistence` store models and the shared `AI.Contracts`
types — everything whose job is to carry named values across a boundary.

A **`record`** is for a type that genuinely wants **value semantics**: it is used as a dictionary or `HashSet` key,
it is copied with `with`, or it is compared structurally (including by a test's equality assertion). When a record
is right, declare it **non-positional** — properties in a body, not a parameter list — so the member list, its
docs and its attributes stay where a reader expects them. **Positional records are avoided** everywhere.

EF entities are classes: an entity has identity, not value equality.

**A response DTO and a hub payload take `required` on every member the server always writes.** Both are built
by the host and only ever read by a client, so a member with no sensible default is `public required T P { get; init; }`
and a member with one carries that value as an initializer instead. On the wire this is a tightening that costs
nothing — the host serializes with `DefaultIgnoreCondition = Never`, so it already writes every member — and it
buys three things: NSwag emits a `required` array for the schema, the generated hey-api types drop their `?:` and
the Zod schemas drop their `.optional()`, and a construction site that forgets a member is a compile error rather
than a silently defaulted value. The frontend consequence is a cost, not a bonus: a test fixture that builds a
partial literal of a generated DTO stops compiling, and a mocked response body that omits a member starts failing
response validation. Completing those fixtures belongs to the same change as the regenerated client. The one
exception is a member the serializer may leave out — `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting…)]` —
which is never `required`, because the schema would demand the key the host is free to omit and the client would
reject the response; `RequiredMemberSerializationTests` refuses the combination.

**A request DTO never takes `required` on a member a client may omit, and never on one bound from the route or
query.** A `required` member turns a tolerated omission into a `JsonException`, which FastEndpoints answers as a
400 the caller never saw before — and no gate catches it, because the OpenAPI document happily describes the
stricter contract. Route and query members are worse than that: the JSON body is deserialized *before* the route
values are overlaid, so a `required` route-bound member rejects every request, including the correct ones. Request
DTOs therefore stay plain `init` members with initializers for their defaults.

*Migration status:* the existing records convert area by area behind a guard that rejects new positional records.
The trap it exists to catch: converting a record to a class silently turns a structural `AssertEx.Equal` into a
reference comparison, so every sub-slice runs the full gate.

### `ConfigureAwait` is contextual

`ConfigureAwait(false)` is written in the **library** projects — `Providers.*`, `AI.Agent`, `AI.Contracts` — which
may be consumed from a caller that has a synchronization context, and every awaited call in one carries it. It is
**not** written anywhere else: the host is ASP.NET Core, the launcher is a console process and the test host is
TUnit, none of which installs a synchronization context, so there the call is noise on every `await` with no
behavioural effect. The two halves are one rule with one enforcement point, `ConfigureAwaitPolicyTests`: it refuses
the token outright in every project that is not a library, and requires a configuration for every awaited call in
the ones that are. The library list in that test is closed, so a project added to the solution is an application
project by default and a new library project has to be named there on purpose.

The disposal of an `await using` **declaration** is outside the rule. Configuring it means letting the declared
variable become a `ConfiguredAsyncDisposable`, which stops the file compiling wherever that variable is then used,
so those declarations in the library projects keep the shape they have. That is also why the type-aware
analyzers are off rather than path-scoped: **CA2007** reports every one of those declarations — its own code fix is
known to produce code that does not compile — and **MA0004** reports the identical set, so neither can gate the
library projects and both stay `none` with that reason recorded in `.editorconfig`.

### Blocking calls and cancellation forwarding are enforced

Meziantou's blocking-call and cancellation rules are build errors in Release (`.editorconfig` sets them to
`warning`, `TreatWarningsAsErrors` promotes them): **MA0042** (a blocking call — `.Result`, `.Wait()`,
`GetAwaiter().GetResult()`, a sync `File`/`Stream`/`Process` API with an async twin, a `using` over an
`IAsyncDisposable` — inside an `async` method), **MA0045** (the same shapes inside a method that could become
async), **MA0079**/**MA0080** (`await foreach` without the in-scope token or `.WithCancellation(...)`),
**MA0040**/**MA0032** (a `CancellationToken` overload exists and the argument was omitted, with and without a token in
scope) and **CA2016** (the CA twin of MA0040, already a warning under `AnalysisMode=All`). Fix shape: `await`, `await
using`, the `*Async` twin, thread the token that is already in scope (`stoppingToken`, a hub's
`Context.ConnectionAborted`, the class's own CTS); pass `CancellationToken.None` explicitly only where no token is
architecturally available (a DI factory delegate, disposal) — the analyzers treat the explicit `None` as the
documented "intentionally not propagating" opt-out. A `#pragma warning disable MA00xx // <reason>` is allowed only where
the sync shape is forced by a contract — sync `Main` (Velopack), `IDisposable.Dispose` drains, a DI factory delegate,
a constructor, a third-party sync member (`TokenCredential.GetToken`, `IChatClient.GetService`), a sync event
handler, a zero-timeout `Wait(0)` poll — one reason per site, restored on the next line after the span. A text-literal
ban on `.Result` would hit the DTO properties named `Result`; these rules are type-aware, which is why they replace a
`BannedSymbols.txt` line.

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

The reverse direction is fenced too: a third-party runtime SDK belongs to the provider that owns it, and
`ThirdPartySdkBoundaryTests` keeps `OllamaSharp`, `Docker.DotNet`, `Azure.*` and `Microsoft.Identity.Client` out
of the host, application, persistence, agent and contracts layers. For Docker and Azure nothing in the project
graph says so on its own, because those packages are referenced by `Client.Application` itself, so every such
reference compiles. That is why the rule is a source scan with its own shrink-only allowlist per SDK. The Docker
entries are the two `ADR 0004` sanctions; the `OllamaSharp` allowlist is empty and stays empty.

A provider whose third-party SDK must not leak marks that `PackageReference` `PrivateAssets="compile"`, and
`LayerDependencyTests` fences a direct re-add in a consumer project the same way it fences `ProjectReference`s —
a per-project allowlist test alone does not stop a transitive compile-asset leak, because a `ProjectReference`
flows the referenced project's package compile assets by default.

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

### Comments and XML documentation

Docs and comments are part of the code's surface: they are read far more often than they are written, and an
oversized one is skipped rather than read. The budgets below are limits, not targets — the best comment is the
one the code made unnecessary.

**`//` comments.** One line preferred, **two consecutive lines maximum**, on its own line above the code it
explains. Say *why*, not *what the next line already says*. A `/* */` block is for a file header, never for
explanation. **No commented-out code** — git has it (Sonar S125 already fails the Release build on it).

**XML documentation.**

| Element | Budget |
|---|---|
| `<summary>` | one sentence, ≤ 240 characters |
| `<param>` / `<returns>` / `<value>` | ≤ 160 characters, and **only when it adds information** the name does not |
| `<remarks>` | only for non-obvious behaviour; ≤ 5 content lines / 600 characters on an ordinary member |
| whole doc block | more than **15 doc lines** on a normal member is a review trigger, not an automatic violation |

Use `<inheritdoc/>` instead of copying an interface's docs onto its implementation. A doc block that has grown
into a design article belongs in `docs/wiki/` or an ADR, with the member's doc reduced to a sentence and a
pointer.

**No history narration.** "Previously we…", "changed in…", a commit SHA — git records that. Design history goes
to `docs/adr/`; architecture goes to `docs/wiki/`. A historically-worded invariant is **rewritten** as the
present-tense rule it actually states, not deleted.

**A remaining TODO states what, why, and a reference.** A bare `TODO`/`FIXME`/`HACK` fails the Release build
(S1135); the fix is to describe the present limitation directly, or to link the ADR/issue that owns it — not to
reword the marker.

**Suppressing a static-analysis finding is the last step, in this order:** understand what the rule is telling
you → fix the root cause → verify the finding is gone → only then suppress, at the **smallest** scope
(`#pragma` around the one span, restored on the next line, or a `[SuppressMessage]` on the one member) **with a
reason**. A file-wide or project-wide suppression, or one with no reason, is a defect.

*Migration status:* slice **S6** lands a shrink-only ratchet on these budgets and then cleans production code in
file-disjoint batches; tests get the ratchet only.

### Tests: TUnit, not xUnit

Backend tests are **TUnit** (`[Test]`) on Microsoft.Testing.Platform, with a project **`AssertEx`** helper
(`AssertEx.Equal/NotNull`) and **NSubstitute** for mocks — **no** xUnit/Shouldly/FluentAssertions/Moq.
Reach for a substitute only after the real thing and the repo's fake seam (`FakeOllama`,
`RecordingHubMessageSender`, MSW) have been ruled out, and never for the gate, cipher or migration the test exists
to verify — [17-writing-tests.md §1a](17-writing-tests.md#1a-test-principles). Every test class carries exactly
one `[Category(TestCategories.…)]`, enforced by `TestCategoryConventionTests` —
[17-writing-tests.md §1b](17-writing-tests.md#1b-test-categories).
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
