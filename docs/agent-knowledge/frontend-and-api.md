# Frontend, chat UX and API boundary

Scope: the React SPA, its tests and tooling, the OpenAPI/hey-api contract and endpoint error shapes. Read when:
changing `XE-Local-AI-Engine.Client.React/`, an endpoint's request/response/error declaration, a hub payload, or
regenerating `openapi/v1.json`. Conventions: `XE-Local-AI-Engine.Client.React/AGENTS.md`; architecture:
`docs/wiki/09-api-and-hubs.md`, `docs/wiki/10-react-client.md`.

## Chat and UI contracts

### Chat rendering contract

**Rule:** render one ordered `parts[]` sequence built by the shared `buildMessageParts()` for live and reloaded
messages alike (the ordered-parts contract). Never flatten reasoning or split streaming/final tool components: one
state-driven `ToolCallCard`, the shared `CodeBlock`. Additive turn metadata stays in `metadata_json`; setting
precedence is request, then conversation, then default. **Prevents:** a live turn and its reload rendering
differently. **Authority:** `buildMessageParts`; `MessageParts.test.tsx`.

### Error surfacing

**Rule:** a failed assistant turn shows exactly one inline red Alert from `hasText(message.error)`, independent of
partial content. Toast = transient mutation result; inline Alert = query error, status, empty guidance or form
validation. Keep i18next `escapeValue: false`. API errors go through `apiErrorMessage(error, localizedFallback)`,
never `error.response.data` (dead once the interceptor replaces the error). **Prevents:** duplicate or blank error
UI and double-escaped entities. **Authority:** `apiErrorMessage`; `ValidationProblemProbeApi.test.ts`;
`BenchmarksPage.test.tsx`.

### Client conventions

**Rule:** chat attachment capability flags are a static client constant in `NodeCapabilities.ts`, not
backend-composed; do not wait for a capabilities endpoint. A bounded Mantine `NumberInput`/`Slider` that persists
"unset" needs a post-mount `ready` guard: Mantine fires a min/default `onChange` on mount and overwrites a deliberate
null. Editing uses the shared Monaco `CodeEditor`: import `editor.api` plus chosen grammars, never `editor.main`;
keep its manual chunk name aligned with the `lazyEditorJavaScriptBytes` matcher or several MiB move into the app
budget. Monaco workers load through Vite `?worker` so the editor stays offline. Chat streaming stays on the lightweight
`CodeBlock`. **Authority:** `NodeCapabilities.ts`; `MonacoRuntime.ts`;
`GraphWorkflowNodeConfigPanel.tsx`; `config/bundle-budget.json`.
[evidence](../agent-knowledge-evidence.md#ui-race-failures)

### Monaco `editor.api` ships no contributions: an option for one draws nothing until it is imported

**Rule:** `MonacoRuntime.ts` assembles `monaco-editor/editor/editor.api.js` (core only: `create`,
`createDiffEditor`, `createModel`, `setModelLanguage`). Any feature that is a contribution (unicode highlighter,
hover, find, folding, …) must be imported from `monaco-editor/editor/contrib/**`, or its options
(`unicodeHighlight`, `renderControlCharacters`, …) are accepted and render nothing, with no warning. Check on the
Vite origin with `monaco.editor.getEditors()[0].getModel().getAllDecorations()`. Each contribution lands in the lazy
editor chunk (`lazyEditorJavaScriptBytes`); the hover alone is about 310 kB. **Prevents:** an "inspect" mode that
passes its unit tests (they assert the options reach `monaco.editor.create`) yet shows an operator nothing.
**Authority:** `MonacoRuntime.ts`; `CodeEditor.test.tsx`; `config/bundle-budget.json`.

### Push first: a `refetchInterval` is a bounded fallback, never the default

**Rule:** state our own process changes reaches the UI over a SignalR hub, with the REST GET as the one-shot hydrate on
mount. A `refetchInterval` is allowed only when (a) the value is sampled and has no event (`model-fit/resources`),
(b) it is the fallback while the hub is degraded, plus a floor of 60 s or slower behind a live hub, (c) it is conditional on an active job no hub covers, or (d) it is a
page-scoped view of state that changes outside the SPA and has no hub, at 5 s or slower. Never a fast poll beside a
live hub that invalidates the same query, app-wide only under (a) or (b); each one carries a comment naming its case.
**Prevents:** an idle header polling residency on every page, and a poll duplicating a mounted hub. **Authority:**
`useSchedulerHub` (`pollIntervalMs`), `useRuntimeResidencyHub`, `Context.ts` (`QueryClient` defaults); wiki 09;
operator decision 2026-10-06.

### Never put `role="button"` on a container that holds other interactive controls

**Rule:** a `<button>` or `role="button"` makes its descendants presentational, so nested menus, actions or links
leave the accessibility tree (axe `nested-interactive`). A row with its own controls keeps `onClick` as a pointer
convenience and promotes its TITLE to the button; a row with nothing interactive inside may be `component="button"`.
**Authority:** `ConversationListItem.tsx`, `TranscriptionSessionList.tsx`, `GraphWorkflowRunList.tsx` and tests.

### A Mantine scroll-area default has to be declared per component key, and a `Modal` only has one if it was given one

**Rule:** `ScrollArea`, `ScrollAreaAutosize` and `TableScrollContainer` are separate theme keys. A `Modal` scrolls a
plain overflow box unless the call site passes `scrollAreaComponent`, as `DialogShell` does. **Prevents:** a theme
scrollbar fix that skips every autosize site and dialog body. **Authority:** `components` block in
`ThemeProvider.tsx`; `ThemeProvider.test.tsx`.

### Judge listbox/option exposure with a verbose a11y snapshot or a DOM role query, never the default snapshot

**Rule:** the default (non-verbose) accessibility snapshot of the browser tooling drops non-focusable descendants of a control, so every `role="listbox"` using the aria-activedescendant pattern (Mantine `Select`/`Autocomplete` or hand-written) shows as an empty listbox while the DOM, `aria-controls`/`aria-activedescendant` and Chrome's real AX tree are correct. Check a verbose snapshot or `querySelectorAll('[role=option]')` before filing an a11y finding. **Prevents:** chasing a phantom "empty listbox" (Mantine `withScrollArea: false` does not change it and regresses the dropdown height). **Authority:** open-items O3/S4 control test (hand-written listbox, focusable vs non-focusable option), 2026-09-28.

### A UI that polls image or whisper residency reads `model-fit/runtime-residents`, never a runtime status route or `isBusy`

**Rule:** read `GET model-fit/runtime-residents` for image and whisper residents (llama.cpp stays on `model-fit/running`; `RuntimeResidencyHub` ticks refresh both, they poll only while it is degraded), never `GET transcription/runtime` or `GET images/runtime`, and never read an activity snapshot's `isBusy` as "work is running" or "eject allowed": it counts a resident daemon, while the row's `canEject` repeats the gate's eviction-reservation refusal. **Prevents:** a per-poll settings load, installed-runtime read and model recommendation from the transcription status route; an eject button disabled whenever anything is loaded. **Authority:** `RuntimeResidentsService`, `TranscriptionRuntimeService.GetRuntimeAsync`, `ImageRuntimeActivitySnapshot.IsBusy`, `TryAcquireEvictionReservation` on both activity gates; wiki 14 and 24, 2026-09-29.

### A wrapper that appears when a field becomes non-empty remounts the input

**Rule:** a Mantine `inputContainer` (or any conditional wrapper around an input) is rendered in the same shape whether the field is empty or set; only its extra children are conditional. Test it by rerendering from blank to set and asserting the input is the same DOM node; a `fireEvent.change` test cannot see a remount. **Prevents:** the input losing focus after the first keystroke, so typing `1500` into an unset field leaves `1` (found only in the browser). **Authority:** `NodeSettingsNumberField`, `NodeSettingsFieldsCard.test.tsx` ("keeps the same input mounted ..."); model-matrix follow-ups, 2026-10-05.

### A gated-off Ollama runtime is a UI state, not an empty list

**Rule:** with `XE_OLLAMA_RUNTIME_ENABLED=false` the no-op runtime answers available-and-empty, which looks idle, so
node settings branches on `ollamaConfigured` via `useOllamaRuntimeConfigured` (in `src/features/node-settings/queries/`), called at the page and passed down as `ollamaRuntimeDisabled`. It fails OPEN:
only a definite `false` hides the endpoint input, never the form model, so the stored value round-trips.
**Authority:** `NodeSettings.tsx`; `NodeSettingsOllamaCard.test.tsx`; `NodeSettings.test.tsx`.

### Races and flashes

**Rule:** auto-advance only after observing unmet then met (arm resets on step change). Globally mounted TanStack
queries need an access-token `enabled` gate, or a pre-login 401 stays cached. react-joyride v3 controlled completion
is final `STEP_AFTER` + `NEXT`, not `STATUS.FINISHED`. Push-only terminal handlers invalidate queries explicitly.
Every `InvocationState` field is copied in `InvocationState.Clone()`, the one deep-copy boundary. **Prevents:**
flashes, stuck 401s, fields that look right live and persist null. **Authority:**
`WorkerEventDispatcher.InvocationState.cs`. [evidence](../agent-knowledge-evidence.md#ui-race-failures)

### an AudioWorklet is ONE self-contained `.js` file imported with `?url`

**Rule:** beyond wiki 24: verify on the emitted asset, since tsc, biome and `vite build` stay green and no gate
instantiates a worklet: `grep -c "import" dist/assets/*Worklet*.js` must be 0, so the file's own comments avoid that
word. **Authority:** `capture/Pcm16DownsamplerWorklet.js`; `capture/Downsample.test.ts`.

## OpenAPI and the generated client

### OpenAPI → hey-api is the sole REST data layer for React

**Rule:** regenerate through `scripts/openapi-live-check.sh` (wiki 09 section 5) and require path removals = 0.
When starting a regen host by hand: `XE_LAUNCH_MODE=desktop`; `ASPNETCORE_ENVIRONMENT=Development` (a Release host
is Production and maps no document: 404 everywhere); read the port from `desktop-port.txt` (desktop mode ignores
`--urls`); with an isolated `HOME`, forward `MISE_TRUSTED_CONFIG_PATHS` and `MISE_DATA_DIR`, or mise aborts the host
before readiness and it reads as a broken spec. **Authority:** `scripts/openapi-live-check.sh`;
`DesktopPortStore.ResolveBindUrlAsync`. [evidence](../agent-knowledge-evidence.md#openapi-incidents)

### hey-api `validator: true` validates requests too, and a `format: binary` body fails as a string

**Rule:** keep `@hey-api/sdk` at `validator: { request: false, response: true }` in `OpenapiTs.config.ts`. Since
hey-api 0.77 `validator: true` runs the zod schema on the request as well, and the zod plugin types `format: binary`
as `z.string()`, so a multipart `File` body sent through the generated SDK (`uploadConversationFile`,
`previewSkillImport`) is refused in the browser before any request leaves, with the misleading message "The server
returned a response in an unexpected shape" and nothing in the network panel. Image, knowledge-base and
transcription uploads post through `axiosInstance` directly and are not affected. **Prevents:** a regenerate that
silently breaks the SDK upload paths while every vitest test (msw never sees the request) stays green.
**Authority:** `OpenapiTs.config.ts`; `openapi:check` in `XE-Local-AI-Engine.Client.React/package.json`.

### A flag-gated route family vanishes from a regenerated spec unless the flag is on

**Rule:** kill switches are request-path middleware, so a disabled family 404s, NSwag emits nothing, and the regen
deletes it. Set the flag on the regen host (`ExternalApps__Enabled=true`). Where services register regardless of a
flag, keep routes discoverable and reject on the request path (Work Sessions); Development Mode is the exception.
**Authority:** `Program.cs` disabled-capability middleware.

### A spec diff that moves schemas is not a loss, and an unannotated member binds from the query anyway

**Rule:** a schema gaining a `$ref` reorders NSwag's components; judge a regen by path sets, not diff hunks.
FastEndpoints binds a member with no `[QueryParam]` on a bodyless verb from the query while the spec calls it a
`requestBody`; endpoint tests pass, only the generated client breaks. Annotate it and assert the location in
`OpenApiDocumentTests`. **Authority:** `UninstallExternalAppRequest.ExpectedVersion`.

### API-boundary traps

**Rule:** beyond wiki 09 (415 body-less POST, `ModelRouteName.Decode`, int64 normalization): multipart DTOs need a
typed `IFormFile? File` so OpenAPI emits multipart, and the client posts `FormData` directly with multipart headers,
bypassing the global Axios JSON default. `ApiError` fallback order is `detail -> message -> title -> ""`. Seeds stay
strings (beyond safe integer). Fix the endpoint or spec, never generated Zod. **Authority:** `FetchOpenapi.mjs`.
[evidence](../agent-knowledge-evidence.md#api-boundary-failures)

### hey-api's generated `queryFn` builds its request from `queryKey[0]`, not from the options it closed over

**Rule:** reusing a generated `*Options()` adapter for another page passes that page's key:
`page.queryFn({ ...context, queryKey: page.queryKey })`. Keys are single-element object arrays: invalidate by
partial-object match, never `.slice()`. **Prevents:** re-requesting page one forever. **Authority:**
`useIntegrationExecutionEvents`; `useIntegrationExecutions.test.tsx`.

### nothing on the axios instance's import path may import the router

**Rule:** `Interceptors.ts` reaches login only through `LoginNavigation.ts` (`navigateToLogin`), filled by
`Router.tsx`'s `registerLoginNavigator`. A static router import closes a cycle through the route tree and
`client.gen.ts` back to `AxiosInstance`, so the hey-api client can be built before `axiosInstance` is set and runs
with no interceptors: no auth header, no ProblemDetails mapping. A lazy `import()` breaks `config/bundle-budget.json`.
**Prevents:** a 409 `conflictType` present in one test file and absent in another. **Authority:**
`LoginNavigation.ts`; `Interceptors.ts` `redirectToLoginOnce`; `ChatWorkflowMode.test.tsx`.

### A FluentValidation range rule is published as OpenAPI min/max even under `.When`

**Rule:** a request field that accepts a sentinel outside its range (`-1` = unset) validates with `Must(...)`, not `InclusiveBetween(...).When(...)`: FastEndpoints publishes the range as `minimum`/`maximum` whatever the condition, and the generated zod schema then refuses the sentinel in the browser before the request is sent. Check the regenerated `zod.gen.ts` for the field, and keep the SPA's range check on the bounds the response carries. **Prevents:** a reset the API accepts and the SPA cannot send. **Authority:** `NodeSettingsEndpointValidators` (`IsUnsetOrBetween`), `StoredNodeSettings.TokenSettingUnset`; model-matrix follow-ups V3, 2026-10-05.

## Endpoint error shapes

### Endpoint exception handling is mature — don't mass-remove catches

**Rule:** before removing a catch, wire the global handler and declare the body actually emitted (the three bodies:
wiki 09 section 4). A new typed conflict = `ConflictExceptionHandler` switch arm + enum +
`ProducesConflictProblemDetails()`. One status with several shapes declares the permissive `ProducesProblem`. Use
the content-type overload of `WriteAsJsonAsync`; the `(value, ct)` one rewrites `application/problem+json`. Over
SignalR, the conflict token starts `HubException.Message`, the only detail forwarded. **Authority:**
`ConflictExceptionHandler`; `ProblemDetailsProducesExtensions`.

### Single-message validation exceptions are mapped globally to 400 — don't re-add per-endpoint catches

**Rule:** `DomainValidationExceptionHandler`'s own `is not (...)` pattern is the inventory; add new single-message
validation there. Keep out `GraphWorkflowValidationException` (per-node keys the canvas renders), any type not 400
at every raising endpoint (`DevelopmentWorkspaceSecurityException`), `SelectedFolderValidationException` (split by
`SelectedFolderExceptionHandler`) and `SlashCommandConflictException` (local 409; moving it changes the wire body).
**Authority:** `DomainValidationExceptionHandler`.

### `DevelopmentWorkspaceSecurityException`: 400 where the request carried the value, 409 where persisted state blocks it

**Rule:** caller-supplied folder defects are 400; persisted project/trust/identity/apply state blocking a valid
request is 409 via `DevelopmentRepositoryStateConflictException`, which derives from the base type so base catches
still work. Reconnect is mixed: status follows the request surface, not the shared throw site. **Authority:**
`DevelopmentRepositoryStateConflictException`.

### Knowledge repository import: only `…ImportRejectedException` is a 400

**Rule:** caller-fixable bound/unavailable failures use `KnowledgeRepositoryImportRejectedException` (400);
host/index/read/race failures use `KnowledgeRepositoryReadException` and stay server errors. Never widen the endpoint
catch to `InvalidOperationException`. **Authority:** `KnowledgeRepositoryImportRejectedException`.

### Before deleting a `.Produces<T>(status)`, trace every exception family the route can raise

**Rule:** a dead `*BlockedResponse` DTO can go, but its `.Produces<>(409)` only after grepping the route's call
chain for every exception type and the handlers `ConfigureServices` registers; then pin statuses in
`OpenApiDocumentTests` with `AssertResponses(paths, path, verb, [...])`. `openapi:check` cannot catch a wrong
declaration. **Prevents:** an undeclared status no SPA handler can target. **Authority:** `TrainingExceptionHandler`;
`OpenApiDocumentTests`.
[evidence](../agent-knowledge-evidence.md#before-deleting-a-produceststatus-trace-every-exception-family-the-route-can-raise)

### A declared 413 is thrown by Kestrel inside model binding, so it needs a handler, not an early exit

**Rule:** beyond wiki 09: `RequestBodyTooLargeExceptionHandler` discriminates on status 413, not the type
(`BadHttpRequestException` also carries 400s owned by other handlers), and sits just before
`DefaultExceptionHandler`. The in-memory test host ignores `IRequestSizeLimitMetadata`, so only a real Kestrel
sees the refusal. Write the 413 only through `RequestBodyTooLargeProblem`, never `Results.Problem` (bare media type,
a second body emitter), and assert the whole content-type with `RequestBodyTooLargeAssert.DeclaredProblemShapeAsync`.
**Authority:** `ExceptionHandlerRegistrationOrderTests`; `RequestBodyTooLargeExceptionHandlerTests`;
`RequestBodyLimitE2ETests` (opt-in).
[evidence](../agent-knowledge-evidence.md#a-declared-413-is-thrown-by-kestrel-inside-model-binding-so-it-needs-a-handler-not-an-early-exit)

### An EF unmapped-type raw SQL query must ALIAS every column, or it binds nothing

**Rule:** `SqlQueryRaw<T>` on an unmapped type binds columns to PROPERTY names; alias every column
(`SELECT graph_json AS GraphJson, ...`). A caller that swallows a read failure logs it at Error with the exception
type. **Prevents:** a run-time throw, or a silent zero-row answer that looks like "nothing to read". **Authority:**
`CanvasWorkflowImport.ReadAsync`; `NodeChatTitleEncryptionBackfillService`.
[evidence](../agent-knowledge-evidence.md#an-ef-unmapped-type-raw-sql-query-must-alias-every-column-or-it-binds-nothing)

### `signalr:check` only diffs hub route strings, never method signatures

**Rule:** beyond wiki 09 ("Adding a hub is two edits"): `pnpm run signalr:check` compares hub paths and hand-written
payload enum lists, never hub method signatures; a signature change needs its own test. **Authority:**
`scripts/CheckSignalrProxySync.mjs`.

## Frontend tests and tooling

### Frontend lint

**Rule:** lint runs `biome format src scripts` as a gate; generated files are excluded and Biome's CSS formatter is
off (stylelint owns CSS). If it fails on a file you did not write, look for gitignored runtime state (a hidden
dot-directory) under `src/` before touching source. react-doctor config is `doctor.config.jsonc`. **Authority:**
`package.json` `lint`; `biome.json`.

### `pnpm run acceptance` refuses a Node major other than CI's

**Rule:** run acceptance on the Node major CI's `client-react` job uses (`node:check` reads it from `.github/workflows/build-and-test.yml`; every job must agree). `package.json` `engines` is only the floor. `XE_ALLOW_NODE_DRIFT=1` runs anyway with a warning; such a run is not CI evidence. **Prevents:** a green local acceptance on a newer major while CI reds on a Node-22-only runtime difference, as develop did on 2026-10-02 (`src/test/JsdomBlobStream.ts`). **Authority:** `scripts/CheckNodeMajor.mjs` (`readCiNodeMajor`, `checkNodeMajor`).

### Frontend tests: an `await import()` inside `it()` is charged to `testTimeout`

**Rule:** keep `testTimeout` at 20 s; use `vi.resetModules()` plus a dynamic import only when module-init hydration
is under test. **Prevents:** a cold dynamic import timing out only under coverage. **Authority:** `vite.config.ts`.
[evidence](../agent-knowledge-evidence.md#browser-and-frontend-timing-measurements)

### A test asserts the bundle string, never an in-code `defaultValue`

**Rule:** beyond the frontend `AGENTS.md`: a file that `vi.mock("react-i18next")`s wholesale renders its defaults
again and loses the coverage; `i18next-browser-languagedetector` writes `i18nextLng` to localStorage during `init()`
(kept, since `UserLanguageStore` seeds from it), so a test asserting empty localStorage clears it itself.
**Authority:** `setupFiles` comment in `vite.config.ts`; `RenderWithProviders.tsx`.
[evidence](../agent-knowledge-evidence.md#a-test-asserts-the-bundle-string-never-an-in-code-defaultvalue)

### An MSW request no handler declared FAILS the test that made it — declare the route, never widen the guard

**Rule:** beyond wiki 17: file-wide routes go in `setupMswServer(...)` defaults; a test that means to make an
undeclared request calls `assertNoUnhandledRequests()`, which asserts and drains. A request fired during RTL
unmount is dropped and charged to no test. When the generated client loses an async hop (the request validators were
dropped on 2026-10-09), a GET that used to fire one tick after the test unmounted fires inside it and hits the strict
guard: register the route in that test, never widen `onUnhandledRequest` or add a wait. **Authority:**
`src/test/UseMswServer.ts`; `ValidationProblemProbeApi.test.ts`; `DevWorkflowDefinitionFormPanel.test.tsx`.

### Every `MantineProvider` a test mounts carries `env="test"`

**Rule:** beyond wiki 17: it also renders an inactive kept-mounted `Tabs.Panel`'s children, so that tab's queries
fire and need routes; with transitions off, a read after a click needs `findBy*`. Tests through the product
`ThemeProvider` keep portals and transitions on. **Authority:** `src/test/RenderWithProviders.tsx`;
`MantineTestRender.tsx`.

### Browser E2E runs as two ordered parallel groups, not one sequential queue

**Rule:** beyond wiki 17: `BrowserSerial` limit 1, `BrowserPooled` limit 4. Keep exactly one `SetupCompleted=true`
user (form login has no email); pooled users use explicit-email API login, whose cookie jar the page shares. Never
seed across phases; never add group/limiter attributes to `XEE2ETestBase`. **Authority:** `BrowserParallelLimit`;
`XESerialE2ETestBase`; `XEPooledE2ETestBase`.
[evidence](../agent-knowledge-evidence.md#browser-and-frontend-timing-measurements)

### `CheckDependencyBaseline.mjs` fails on a new `warn` fingerprint, so a `no-orphans` warning is a gate failure

**Rule:** any new fingerprint fails, `severity: "warn"` included. A new orphan gets one targeted `pathNot` on
`no-orphans` in `.dependency-cruiser.cjs`, never a fingerprint in `config/dependency-baseline.json`; test imports
are not edges. The `**/*Worklet.js` `javascript.globals` override in `biome.json` stays LAST: a later override's
`globals` replaces an earlier one's (it declares `AudioWorkletProcessor`/`registerProcessor`/`sampleRate` for
`noUndeclaredVariables`). **Prevents:** a warn-level orphan passing locally and failing the gate, and worklet
globals silently undeclared. **Authority:** `evaluateDependencyBaseline` in
`scripts/CheckDependencyBaseline.mjs`; `biome.json`.

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| `nodeCapabilities.preview` gates a route. | The flag, the route and the feature are gone; `graphWorkflows` is the successor and is on by default (§5). |
| Desktop-only ThemeConfigurator/Open Canvas are outside the mobile-responsive scope. | Both are gone; no surface is excluded from the mobile-responsive scope (§6). Theme is Mantine-native, with scheme and accent in the node-settings "This browser" card. |
| A capped route's 413 comes in FastEndpoints' `errors[]` shape when the endpoint refuses it itself. | Both the host's and the endpoint's refusal write the same ASP.NET `ProblemDetails` body via `RequestBodyTooLargeProblem` (§5). |
| Date sites outside `formatTimestamp` were deliberately left on the browser locale. | No site is. S7-C moved the last four — the chat clock, the conversation-list day, the model-fit catalog release date and the usage dashboard's day label — onto `formatTimestamp`/`formatTime` through their optional `Intl.DateTimeFormatOptions` parameter (§5, wiki 10). |
