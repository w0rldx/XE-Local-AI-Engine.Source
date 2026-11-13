# Quality review

> Reference page for [Testing & Validation](../13-testing-and-validation.md) "Quality review" · Reviewed: 2026-11-13 · Code-grounded.

It holds the method for a read-only quality review of code or docs: the source order, the procedure, severity, the
report format, the house review checklist and the applicability profiles for the external guideline sets. It points
at rule homes; it does not restate them.

## Purpose and source order

A review is evidence-based and read-only: no source edits, mutating commands or builds unless the operator asked
for them. It produces findings and a remediation plan, not fixes. Sources, highest first:

1. Security and correctness: verified contracts and observed runtime behaviour.
2. House rules: `AGENTS.md`, `XE-Local-AI-Engine.Client.React/AGENTS.md`, [agent-knowledge](../../agent-knowledge.md)
   (§0, the routing table, then the touched topic file and its stale-beliefs table), the wiki ([16](../16-code-conventions.md)
   conventions, [17](../17-writing-tests.md) tests, [13](../13-testing-and-validation.md) validation,
   [12](../12-security-and-privacy.md) "Invariant checklist (for reviewers)", [08](../08-data-and-persistence.md)
   persistence, [09](../09-api-and-hubs.md) API and hubs) and `docs/adr/`.
3. Official vendor documentation for the installed version (.NET, ASP.NET Core, Microsoft.Extensions.AI, Microsoft
   Agent Framework, React, Mantine, TanStack). Read the version from `Directory.Packages.props` or `package.json` first.
4. External guideline sets: advisory only, applied through the [profiles](#guideline-applicability-profiles) below.

A conflict between sources is reported as a conflict, citing both; the lower source is never applied silently.

### External guideline sets

| Set | Published as | Applies to | Pinned |
|---|---|---|---|
| React performance | Vercel React best-practices rules | `XE-Local-AI-Engine.Client.React` | `skills-lock.json` |
| Web interface | Vercel Web Interface Guidelines | SPA UI, shared primitives first | `skills-lock.json` |
| Modern web platform | Google Chrome modern-web-guidance | SPA HTML, CSS and DOM; untrusted markdown | `skills-lock.json` |
| Writing | Vercel writing guidelines | Docs; UI copy in `src/locales/en.json` | `skills-lock.json` |
| C# | the dotnet C# skill and the specialists it routes to | .NET projects | not pinned |

The report names the lock hash (or fetch date) of each set it used. When a set's hash changes, classify its new rules
before applying them.

Browser target: Vite's default build target (Baseline Widely available). The app also runs in WebView2 and WebKitGTK
([Hosting & Deployment](../11-hosting-and-deployment.md)), so Safari-class gaps count. A feature beyond the target
needs a fallback or is not proposed.

## Procedure

1. **Scope.** Name the branch and commit, the area or diff, and the guideline sets that apply. Confirm the worktree
   with `git status --short --branch`.
2. **Read the homes** for the touched area (source order 2). Before a "missing feature" finding, read agent-knowledge
   §6 "Deliberately NOT built".
3. **Skip what tools already fail on** ([Enforced by tooling](#enforced-by-tooling)); spend the effort on the
   review-only items.
4. **Apply the review-only checklist** for the touched layers.
5. **Apply external rules** only where a profile says Applicable or Applicable with adaptation. Not applicable and
   Conflicts verdicts are never findings.
6. **Verify every candidate** in the code (read the symbol, follow its callers). Keep observed defects apart from
   hypotheses that need a profile, a browser or a test.
7. **Write the report** to the location the operator names.

## Severity

| Level | Meaning |
|---|---|
| P0 | Exploitable security-boundary failure, secret exposure, unsafe tool, path or process execution, data loss |
| P1 | Proven functional, cancellation, disposal or concurrency flaw; a user group blocked (keyboard trap, unlabeled control in a core flow); broken authorization; a proven defect without a regression test |
| P2 | Maintainability, performance or accessibility issue backed by evidence; code drifting from a house rule |
| P3 | Polish, optional modernization, unmeasured optimization |

Style the analyzers already decide is never P0 or P1.

## Report format

1. **Scope**: branch, commit, areas read, package versions, guideline sets with lock hash or fetch date.
2. **Findings**, P0 to P3. Each names the rule and its source, the **file + symbol** (never `file:line`), a short
   quote, the impact, the smallest fix that follows an existing repo pattern, the confidence, and whether it is
   observed or a hypothesis. Repeated instances are one finding with examples.
3. **Rule-vs-code drift**: house rules the code broadly does not follow, with the question "enforce with a gate or
   change the doc?" instead of one finding per instance.
4. **Coverage**: what was read, what was skipped, blind spots (no runtime, browser or profiler evidence).
5. **Validation**: commands actually run and their result. A timeout, exit 69 or 75, exit 8 or zero tests is not a
   pass, and a Debug build is not analyzer evidence ([Validation commands](../13-testing-and-validation.md#validation-commands)).
6. **Remediation**: behavioural fixes first, then refactors, performance experiments and cleanup; each sized and tied
   to the gate that proves it.

With no findings, say what was checked, not that the area conforms. The absence of a guideline pattern is not a
defect without a concrete cost. Never invent line numbers, counts, results or speed-ups.

## House review checklist

One line per rule with its home; the home wins when a line looks stale. W16, W17, W13, W12 are the wiki pages;
W10 is the React client page; AK is `docs/agent-knowledge/<topic>.md`; Arch is the `Architecture` namespace in
`XE-Local-AI-Engine.Tests`. "This page" marks a review-only check with no other written home.

### Enforced by tooling

Do not re-review these; flag only a new suppression or allowlist growth.

| Gate | What it decides | Home |
|---|---|---|
| Release analyzers | Cancellation forwarding, sync-over-async, disposal, bare TODO markers, `[GeneratedRegex]` timeout. Debug skips some analyzers: only a Release `--no-incremental` build is evidence | AK build-and-analyzers, `.editorconfig` |
| `BannedSymbols.txt` | Ambient time, `Thread.Sleep`, `GC.Collect`, Moq and FakeItEasy | W12 §8 |
| Arch tests | `ConfigureAwait` policy, primary constructors, `ThrowIfNull` guards, positional records, tuples, `Try*` shape, `DbContext` fence, layer and SDK boundaries, endpoint conventions and authorization, exception contracts, file placement, comment budgets, test categories | W13 "Architecture guards" |
| `*Allowlist.txt` | Shrink-only: an added entry is a finding | each allowlist's test |
| Frontend lint | Biome (a11y rules see plain HTML elements only, not Mantine components), Knip and dependency-cruiser baselines, i18n default and locale-sort checks, `CheckTestsHaveAssertions.mjs`, `openapi:check`, `openapi:breaking` | `XE-Local-AI-Engine.Client.React/AGENTS.md` |
| `scripts/docs-inventory-check.py` | Citations, tool names, host phrasing, plan references, tracked secrets, inventories, links, agent-knowledge caps | its header |

### Review-only

| Area | Check | Home |
|---|---|---|
| C# | DTOs are `sealed class` with `required`/`init`; a `record` only for value semantics, never positional | W16 "DTOs, records and type choice" |
| C# | A refactor adds no null guard that was not already there | W16 "DI + class house style" |
| C# | `ConfigureAwait(false)` only in the library projects | W16 "`ConfigureAwait` is contextual" |
| C# | Absence: `T?`, `Try*` with `[NotNullWhen(true)] out`, outcome types, not-found exceptions; `!` on a nullable is a defect | W16 "Absence: …" |
| C# | Many constructor dependencies is a responsibility smell | W16 "DI + class house style" |
| C# | A catch-all states its reason; `OperationCanceledException` is never swallowed as a failure | W12 "Verbose logging and the silent-catch policy" |
| C# | Background work: `Progress<T>` has no sync context; a detached `_ =` task observes its own faults; a `TaskCompletionSource` wait completes on cancel | this page |
| C# | A long-lived `HttpClient` sets `SocketsHttpHandler.PooledConnectionLifetime` | this page |
| C# | DI lifetimes: scope validation runs only in Development and opted-in tests, so review registrations | this page |
| Persistence | Data access is a `*Store`; no `IRepository`; no `DbContext` in an Application service | W16 "The repository abstraction is `*Store` …" |
| Persistence | Reads `AsNoTracking`; set operations `ExecuteUpdateAsync`/`ExecuteDeleteAsync`; no N+1; projection and paging | W16 "Persistence: …" |
| Persistence | Fixtures keep SQLite foreign keys on, as the node does | AK backend-tests-conventions |
| Persistence | Sibling migrations are not cumulative; designer files are generated; a migration test asserts at its own id; a new stored setting runs `SettingsReview_CoversEveryStoredField_ExactlyOnce` | W17 "An EF migration", `AGENTS.md` "Validation" |
| Security | Every outbound connection traces to an operator-enabled feature | W12 §1 |
| Security | No secret reaches the browser, logs, transcripts or DTOs; credential fields go through a redactor | W12 §2.2 |
| Security | At-rest crypto only through `AesGcmNodeAeadCipher` with its AAD; secret files 0600 at create; one protector per purpose | W12 §2.3, §5 |
| Security | Production errors carry no internal detail; endpoints do not hand-roll catch blocks | W12 §4 |
| Security | Privacy-sensitive runs stay node-local; cloud gates key on `IModelTrustResolver` | W12 §6 |
| Agents | A tool handler never blocks on a human; a throwing handler gives the model a blind error | AK agents-and-sandbox |
| Agents | Every tool call goes through `IToolInvocationService.InvokeAsync`; approval policy only tightens | AK agents-and-sandbox |
| Agents | Paths resolve through `ResolveJailPath`, containment only through `PathContainment.IsUnderRoot`; check TOCTOU and redirect-hop DNS | W12 §7, AK sandbox-and-compute |
| Agents | Steps, tool calls, tokens, jobs, queues, process lifetime and captured output are bounded | AK agents-and-sandbox |
| Agents | A tool-schema or llama.cpp pin change runs the tool-grammar smoke with its failing negative control | `AGENTS.md` "Validation" |
| API | FastEndpoints, one sealed endpoint per file, routes from `LocalApiRoutes` | W16 "Endpoints: FastEndpoints, …" |
| API | Settings PATCH: JSON null clears, omitted keeps, `-1`/`""` is 400 | AK frontend-and-api |
| API | A new hub is mapped, proxied and named in the wiki API page | [09](../09-api-and-hubs.md) |
| API | A contract change regenerates and commits the client and the OpenAPI spec | `AGENTS.md` "Working rules" |
| API | Streams: cancellation, ordering, a terminal frame, bounded buffers, no success before the work succeeded | this page |
| React | Data through the generated client; server state in TanStack Query; Zustand atomic selectors; push first | `XE-Local-AI-Engine.Client.React/AGENTS.md` "Conventions" |
| React | Forms are Mantine + `useState` + Zod on submit | W16 "Forms are manual — no form library" |
| React | No literal user-facing string; German uses "Sie"; errors through `apiErrorMessage` | W16 "No user-facing string is a literal" |
| React | Breakpoints from `LayoutBreakpoints.ts`; components and hooks are named function declarations | W16 "Responsive layout: …", "Components and hooks are …" |
| React | Load-bearing lint suppressions stay | W16 "Load-bearing suppressions — do not "fix" these" |
| React | React Compiler is off: memoization rules apply as written | [React performance](#react-performance) |
| React | Streaming views batch per frame; renderer maps passed to markdown are stable (otherwise every paragraph remounts) | this page |
| React | Dialogs have an accessible name; destructive actions confirm through `useConfirm` | W10 "Unified dialog system"; naming: this page |
| React | UI evidence names its origin (Vite dev origin or the built SPA) | `AGENTS.md` "Local runtime" |
| Tests | TUnit on Microsoft.Testing.Platform; one `[Category]` per class | W17 "1b. Test categories" |
| Tests | Self-validating; a skip is visible; mock order; no sleeps; global state restored in `finally` | W17 "1a. Test principles", "4. Never wait with `Task.Delay`" |
| Tests | Scope with `--treenode-filter`; zero tests or exit 8 is never a pass | `AGENTS.md` "Validation" |
| Docs | House style beats external writing rules: no `file:line`, no heading renames, no count quoted as current, one home per rule | `AGENTS.md` "Working rules", Home "How to read this wiki" |
| Docs | A behaviour or contract change updates its wiki home and every enumerating page | `AGENTS.md` "Validation" |
| Process | Conventional Commits, body only, no AI or session trailers; never set git identity | `AGENTS.md` "Working rules" |

## Guideline applicability profiles

Verdicts: **Applicable**; **Applicable with adaptation** (the adaptation is stated); **Not applicable** (reason);
**Conflicts with** a house rule (cited, house wins); **Enforced by** a tool (named). Re-classify a rule when the set's
lock hash changes or when the house rule it cites changes.

### React performance

Deciding facts: React Compiler is off (`vite.config.ts` calls `viteReact()` with no compiler); SPA only, no SSR,
RSC or hydration; route code splitting is automatic (`autoCodeSplitting`); server state is TanStack Query.

| Rule group or rule | Verdict | Reason |
|---|---|---|
| `async-cheap-condition-before-await`, `async-defer-await`, `async-parallel` | Applicable | Route `beforeLoad`, mutation handlers, invalidations |
| `async-dependencies` | Applicable with adaptation | Principle only; adding the recommended library fights the Knip baseline |
| `async-api-routes`, `async-suspense-boundaries` | Not applicable | No Next.js routes; data uses non-suspense `useQuery` |
| `bundle-barrel-imports` | Applicable | Also a house rule: W16 "Feature-folder layout" (no `index.ts` barrels) |
| `bundle-analyzable-paths`, `bundle-defer-third-party`, `bundle-conditional`, `bundle-preload` | Applicable | Static shell and landing route imports |
| `bundle-dynamic-imports` | Applicable with adaptation | Read `next/dynamic` as `React.lazy` plus route splitting; budgets in `config/bundle-budget.json` |
| `server-*` | Not applicable | No RSC, SSR, server actions or `after()`; browser module state is per-user |
| `client-swr-dedup` | Conflicts with W16 "Data layer = the hey-api generated client, …" | TanStack Query already dedupes; SWR is not adopted |
| `client-event-listeners`, `client-passive-event-listeners`, `client-localstorage-schema` | Applicable | Window listeners, scroll latches, persisted stores |
| `rerender-memo`, `rerender-memo-with-default-value`, `rerender-functional-setstate`, `rerender-lazy-state-init` | Applicable | No compiler; extracting memoized parts of a god component follows W16 "God-component decomposition is a reviewed pass, not a drive-by" |
| `rerender-dependencies` | Enforced by Biome | `useExhaustiveDependencies` |
| `rerender-derived-state` | Applicable | Store half is already a house rule (atomic selectors) |
| `rerender-move-effect-to-event` | Applicable with adaptation | `doctor.config.jsonc` allows a form reporting dirty flags to its page from an effect (unsaved-changes guard) |
| Other `rerender-*` | Applicable | |
| `rendering-hydration-no-flicker` | Applicable with adaptation | No hydration; the analog is a pre-paint script for client-only state such as the colour scheme |
| `rendering-hydration-suppress-warning`, `rendering-resource-hints`, `rendering-script-defer-async` | Not applicable | No hydration; same-origin loopback app with no third-party origins or scripts |
| Other `rendering-*` | Applicable | |
| `js-flatmap-filter` | Enforced by Biome | `useFlatMap` |
| Other `js-*` | Applicable | Low priority |
| `advanced-*` | Applicable | Hub hooks reading callbacks through refs are load-bearing (W16 "Load-bearing suppressions …") |

### Web interface

| Rule | Verdict | Reason |
|---|---|---|
| Icon-only buttons named, form controls labelled, semantic HTML, headings and skip link, `aria-live` for async updates | Applicable | Biome sees only DOM elements, so Mantine components need review |
| Key handlers on interactive elements, image `alt`, clickable labels | Enforced by Biome and Mantine | `useKeyWithClickEvents`, `useAltText`, `noLabelWithoutControl` |
| Media captions and controls | Not applicable | No media elements; `useMediaCaption` is on |
| Visible focus, `:focus-visible`, no bare `outline: none`, sticky bars not covering focus | Applicable | Mantine `focusRing: "auto"` covers the ring; the rest is not tool-checked |
| `autocomplete`, input `type`/`inputmode`, `spellCheck={false}` on codes, no paste blocking, inline errors, unsaved-navigation warning | Applicable | |
| Submit enabled until the request starts | Conflicts with W10 "Forms (Zod, controlled inputs)" | Validate-then-submit; a no-op save is disabled on purpose |
| Placeholders end with `…` plus an example | Not applicable | House placeholders are example values; changing that is an operator decision |
| `prefers-reduced-motion`, transform/opacity animation, no `transition: all` | Applicable | framer-motion honours the user setting app-wide; Mantine transitions need checking |
| `…` not `...`, curly quotes, non-breaking unit spaces, `tabular-nums`, loading states ending with `…` | Applicable | UI copy |
| Long content, `min-w-0`, empty states | Applicable | |
| `<img>` dimensions and lazy loading | Applicable | |
| Above-fold `fetchpriority`, `preconnect`, font preload, video over GIF | Not applicable | No LCP image, same-origin app, no web fonts or GIF assets |
| Virtualize long lists, no layout reads in render | Applicable | |
| Prefer uncontrolled inputs | Conflicts with `XE-Local-AI-Engine.Client.React/AGENTS.md` "Conventions" (forms are manual) | Controlled Mantine + `useState` |
| URL reflects state, links for navigation, destructive actions confirm | Applicable with adaptation | Router search params instead of nuqs; `useConfirm` is the house primitive |
| Tap highlight, `touch-action`, modal overscroll, `color-scheme` | Enforced by Mantine | |
| `env(safe-area-inset-*)` | Not applicable | No `viewport-fit=cover` |
| Flex/grid over JS measurement | Conflicts with W16 "Responsive layout: breakpoints come from `LayoutBreakpoints.ts`" | Measurement switches component trees |
| `theme-color` meta matching the background | Applicable | |
| Dates and numbers through `Intl`, `translate="no"` on code tokens | Applicable with adaptation | Dates go through the house formatters (W10 "i18n") |
| Hydration safety | Not applicable | Client-only SPA |
| Title Case headings and buttons | Not applicable | The UI is sentence case and German capitalises its own way; changing that is an operator decision |
| Second person, numerals for counts, active specific labels | Applicable | German uses formal "Sie" |
| Output as `file:line` | Conflicts with `AGENTS.md` "Working rules" | Cite file + symbol |

### Modern web platform

| Guide or area | Verdict | Reason |
|---|---|---|
| IME-safe Enter submit, forms, sign-in autofill | Applicable | Chat composers, rename fields, login and vault forms |
| Untrusted HTML sanitizing, security, privacy, Trusted Types | Applicable | Model output rendered as markdown is the main untrusted input |
| Global events and differentials (Intl parts) | Applicable | Timestamps, durations, scheduler time zone |
| Dark mode, toggle, browser UI theming | Applicable | Light/dark/auto toggle |
| Navigation drawer, light-dismiss dialog | Applicable with adaptation | Only for hand-rolled overlays; Mantine owns the rest |
| Native `<dialog>`, `popover`, anchor positioning, invoker commands, `interestfor`, custom `<select>` | Conflicts with W10 "Unified dialog system" | Mantine owns overlays; several features are beyond the browser target |
| Validation timing, required-field feedback | Conflicts with W16 "Forms are manual — no form library" | Zod on submit |
| Container queries | Conflicts with W16 "Responsive layout: breakpoints come from `LayoutBreakpoints.ts`" | JS measurement switches component trees, not only styles |
| View transitions | Not applicable | framer-motion handles motion; WebKitGTK support varies |
| `content-visibility` | Not applicable | The chat list is virtualized |
| `fetchpriority`, background-fetch priority, image priority | Not applicable | The generated client uses XHR; images load as authed blob URLs; no LCP image |
| PWA install and origin migration | Not applicable | Per-install loopback port; desktop users get a native shell |
| Built-in browser AI | Not applicable | Chrome-only; the product is the local inference engine |
| WASM, WebMCP | Not applicable | No client native code; the node is already an MCP server |
| Passkeys | Not applicable | An IP address is not a valid WebAuthn RP ID |
| Payments, OTP, carousels, parallax, cross-document transitions, external subresources | Not applicable | No such UI or subresources |
| Connection allowlist header | Not applicable | Backend header without Firefox or Safari support; a CSP is the alternative |
| `Temporal`, `Intl.DurationFormat`, Sanitizer API, `closedby`, `hidden="until-found"`, scroll-driven animations | Applicable with adaptation | Beyond the browser target: only with the guide's fallback |

### Writing

Audiences: Agent (`AGENTS.md`, agent-knowledge, the shipped skill), Dev (README, CONTRIBUTING, wiki, ADRs), User
(`docs/user-guide/`, UI copy).

| Rule | Verdict | Reason |
|---|---|---|
| Reference a page plan | Conflicts with `AGENTS.md` "Working rules" | `plan-references` bans citing the git-ignored plan folder |
| `meta.*` frontmatter, `<Steps/>`, line highlights, dashboard links, model-catalog strings | Not applicable | GitHub Markdown, no docs site; the wiki opener is the equivalent |
| One job per page | Applicable | Lookup material lives in `docs/wiki/reference/` |
| User-shaped titles | Applicable with adaptation | User docs only; wiki and ADR titles are reference titles |
| Active voice, imperative steps, present tense, limited "we", no rhetorical questions, second-read test, earn every detail | Applicable | Present tense matches W16 "Comments and XML documentation" (no history narration) |
| Direct "you" | Applicable with adaptation | "Operator" names a role, not a stand-in for "you" |
| Contractions | Applicable with adaptation | User docs and UI only |
| Sentences under 20 words | Applicable with adaptation | Prose only; dense agent tables are exempt |
| Banned words (easy, simple, just, very) | Applicable | Technical terms such as option values are exempt |
| Replace weasel words with a figure | Conflicts with `AGENTS.md` "Working rules" (no count quoted as current) | Use a qualitative claim or the command that measures it |
| AI tells | Applicable | Spec-sheet voice is fine in reference tables |
| Sentence-case headings, descriptive subheadings | Applicable with adaptation | New headings only: Home "How to read this wiki" forbids renaming; ADR template headings stay |
| TL;DR opener, section summaries, defined terms | Applicable | The wiki opener and agent-knowledge §0 serve as openers |
| Acronyms spelled out | Applicable with adaptation | README, Home and user docs |
| Paragraphs of 2–4 sentences | Applicable with adaptation | An agent-knowledge entry is one capped paragraph (agent-knowledge "Maintenance") |
| List format | Applicable | Pick one lead-in style per page |
| Fence language tag, explain every code block, snippets of 25 lines or fewer | Applicable | |
| TypeScript by default, 80-column snippets | Not applicable | Backend is C#; commands are copied verbatim |
| Minimal comments in code blocks | Not applicable | House command blocks explain each command with a trailing comment |
| Inline examples instead of pointers | Conflicts with `AGENTS.md` (one home per rule, script headers document flags) | Pointing is the house design |
| `snake_case` placeholders | Applicable with adaptation | Credential values in user docs and the skill; `<branch>`-style shell placeholders stay |
| Spaced units (`64 KB`) | Applicable | |
| Bare seconds (`30s`) | Not applicable | House form is spaced (`5 s`) |
| Bold for UI elements or critical facts | Applicable | Structural lead-ins such as `**Rule:**` are exempt |
| No em dashes | Applicable with adaptation | UI copy and user docs; do not churn the wiki |
| Curly quotes | Applicable with adaptation | UI copy only; agents grep exact strings in Markdown |
| `…` not `...` | Applicable | UI copy, including loading states |
| Don't hard-wrap | Not applicable | `.editorconfig` caps Markdown lines; the corpus mixes both; an operator decision |
| Anchor text names the destination | Applicable with adaptation | The `wiki NN §M` citation form stays (Home "Conventions in this wiki") |
| Disclose AI use, name editor tools | Conflicts with `AGENTS.md` "Working rules" | No AI trailers; no per-user tooling named |
| "Can a model complete the goal from this page" test | Applicable | Highest value for agent docs |
| Output as `file:line` | Conflicts with `AGENTS.md` "Working rules" | Cite file + symbol |

### C#

| Guidance | Verdict | Reason |
|---|---|---|
| Route to the narrowest specialist | Applicable | EF Core, performance, refactoring, tests, MSBuild |
| Web API skill for endpoints | Conflicts with W16 "Endpoints: FastEndpoints, one per file — **not** MediatR/CQRS" | Enforced by `EndpointConventionTests`, `EndpointDependencyTests` |
| Breaking-change review | Not applicable | No public API surface; the contracts are OpenAPI (`openapi:check`) and the hub payloads |
| Build the narrowest project | Conflicts with `AGENTS.md` "Validation" | Release backend gate, build lock, `--treenode-filter` |
| Do not raise SDK, TFM, LangVersion or analyzers | Enforced by `global.json`, `Directory.Build.props` | |
| Do not edit generated files | Enforced by `AGENTS.md` "Working rules", `.editorconfig` | Migrations are `generated_code` |
| Cancellation forwarding, no sync-over-async | Enforced by Release analyzers | W16 "Blocking calls and cancellation forwarding are enforced"; unlinked tokens remain a review item |
| Disposal and ownership | Applicable | CA2000 and CA2025 are on; undisposed fields (CA2213) are only a suggestion |
| No `!` or broad casts | Applicable | Nullable warnings are errors; no analyzer catches `!` (W16 "Absence: …") |
| No catch-all handlers | Conflicts with W12 "Verbose logging and the silent-catch policy" | A catch-all at a detached boundary is allowed with a reason |
| No fire-and-forget hiding failures | Applicable | Not enforced; a detached task must catch everything and write a terminal status |
| `async void` | Applicable | Not enforced; event handlers only |
| `ConfigureAwait` everywhere | Conflicts with W16 "`ConfigureAwait` is contextual" | Enforced by `ConfigureAwaitPolicyTests` |
| Modern syntax (primary constructors, positional records, tuples) | Conflicts with W16 "DTOs, records and type choice" | Enforced by the Arch convention tests |
| Guard clauses | Applicable with adaptation | `ThrowIfNull`, never `?? throw` (W16 "DI + class house style"); enforced by `ArgumentNullGuardConventionTests` |
| Ambient time, `Thread.Sleep`, `GC.Collect` | Enforced by `BannedSymbols.txt` | |
| DI lifetimes | Applicable | Scope validation runs in Development and opted-in tests only |
| `DbContext` ownership and concurrency | Applicable | Ownership enforced by `ApplicationDbContextFenceTests`; concurrent use is a review item |
| Exceptions at boundaries | Enforced by Arch tests | `ExceptionHandlerCoverageTests`, `ExceptionInnerExceptionContractTests` |
| Logging templates | Applicable | CA2254 is an error; CA1848 is off by decision |
| Comments, TODOs, dead code | Enforced by analyzers and `CommentBudgetConventionTests` | |
