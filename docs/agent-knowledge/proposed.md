# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Removing a type can trip the file-placement allowlist, and only a direct test-host run names the entry

**Rule:** after deleting or moving a type out of a multi-type file, run `FilePlacementConventionTests` through the native test host with `--treenode-filter`; `TheAllowlist_HasNoStaleEntry` fails for the now-single-type file, and the gate log shows only the failure, not the stale path. Delete the line from `Architecture/FilePlacementAllowlist.txt`. **Prevents:** a red gate after a clean Debug run, and guessing which allowance went stale. **Authority:** `8655fa109` (BenchmarkKldBaseCache.cs allowance removed), node-settings round 2. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Run CommentBudgetConventionTests in the focused Debug pass before the backend gate

**Rule:** after adding comments or XML docs, run `CommentBudgetConventionTests` with `--treenode-filter` in Debug before queueing `scripts/run-backend-tests.sh`: an own-line `//` run longer than two lines, a `<summary>` over 240 characters, or a now-stale `CommentBudgetAllowlist.txt` entry fails it. **Prevents:** a full Release gate spent to learn a comment is one line too long. **Authority:** `8385432c8` (accessor comments trimmed to the budget), node-settings round 2 S1. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### `NodeSettingsEndpointDtoMapper.ToStoredSettings` must copy every new stored field

**Rule:** `ToStoredSettings` builds a NEW `StoredNodeSettings` record from the request and the current settings; a new stored field needs its `request.X ?? currentSettings.X` line there in the same change, with an endpoint round-trip test that saves a DIFFERENT field and asserts X survived. **Prevents:** a UI save of any unrelated setting writing the record without X, which wipes the stored value back to its seed. **Authority:** `NodeSettingsEndpointDtoMapper.ToStoredSettings`, node-settings tier B S1. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### `AddServices` registration tests run at `--maximum-parallel-tests 1`

**Rule:** run tests that build the full `ConfigureServices.AddServices` container (Development endpoint registration, hub inventory, feature-switch composition) with `--maximum-parallel-tests 1` when iterating; they share process-global FastEndpoints serializer options and Serilog static state. **Prevents:** intermittent failures in a focused run that look like a product regression and vanish at width 1. **Authority:** memory of FastEndpoints `SerOpts` being process-global; node-settings tier B S1 worker report, 2026-10-02. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Run the frontend gate on CI's Node major before hand-off, never only on the box's newer Node

**Rule:** CI runs `client-react` on Node 22 (`.github/workflows/build-and-test.yml`, `node-version: 22`) while the development box runs Node 24; a green local `pnpm run acceptance` on 24 is not CI evidence. Run it through `mise exec node@22 -- pnpm run acceptance` (or pin 22 for the gate) before hand-off. **Prevents:** a merge that lands green locally and reds develop on Node-22-only runtime differences: undici in 22 treats jsdom's `Blob` (has `arrayBuffer()`, no `stream()`) as a Blob and throws `object.stream is not a function` for every axios `responseType: "blob"` request MSW's XHR interceptor answers; 24 stringifies it and passes. **Authority:** `src/test/JsdomBlobStream.ts`, `JsdomBlobStream.test.ts`; develop run 37045197138 red after e2fa46635 (image edit), 2026-10-02. Target: frontend-and-api.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### A publish include over a repo tree excludes every hidden directory, not a list of known ones

**Rule:** a `Content Include="…/tools/<x>/**"` that ships a repo tree into the publish layout excludes `**/.*/**` (every dot-prefixed directory) plus `__pycache__`, never an enumerated list such as `.venv` and `.*_cache`. Prove it with `dotnet msbuild <Client.csproj> -getItem:Content -p:Configuration=Release` after planting a probe hidden directory, and with the unfixed project as the negative control. **Prevents:** local, git-ignored state (editor or agent session directories, caches that did not exist when the list was written) riding into a tester build — a 2026-10-02 tester zip carried one under `training-scripts/`. **Authority:** `XE-Local-AI-Engine.Client.csproj` (the training and compute `Content` includes); tester round 6, 2026-10-03. Target: ci-and-release.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Regenerate the OpenAPI client only after a Release build of the contract change

**Rule:** `OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh` starts its host from the Release output with `--no-build`, so a contract change built only in Debug regenerates against the old binaries and reports PASS with no diff. Build the solution in Release first, then confirm the regen diff names the new members. **Prevents:** committing a contract change with a stale client, or reading "no diff" as "nothing to regenerate". **Authority:** `scripts/openapi-live-check.sh` ("Release, --no-build"); model-matrix Track 1a, 2026-10-04. Target: frontend-and-api.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### A singleton that needs a scoped service resolves it from its own scope; unit tests cannot see the lifetime

**Rule:** a singleton (the background model agents, workers) that needs a scoped service such as `IModelCapabilityResolver` takes `IServiceScopeFactory` and resolves it in a per-call `CreateAsyncScope()`, never by constructor. A constructor-injected scoped dependency passes every unit test that `new`s the class and fails only host DI validation (`Cannot consume scoped service ... from singleton`). **Prevents:** a branch whose focused tests are green and whose host does not boot. **Authority:** `DefaultMemoryExtractionAgent`, `DefaultPlaybookAnalysisAgent`; caught by the OpenAPI regen host, model-matrix Track 1a, 2026-10-04. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### FastEndpoints validator 400s carried a trace id that never joined the log

**Rule:** FastEndpoints' default `ProblemDetails` `ResponseBuilder` writes `HttpContext.TraceIdentifier` (the Kestrel connection id), not the W3C trace id the log template prints, so any 400 the framework writes itself needs `FastEndpointsProblemWriter.Build` set as `ResponseBuilder` (`UseProblemDetails` in `Program.cs`). **Prevents:** a user quoting the `traceId` of a validation error and no `[trace:…]` log line ever matching it, while handler-written problems did match. **Authority:** `FastEndpointsProblemWriter.Build`, `ProblemDetailsExtensions.ResolveTraceId`; logging checkup 2026-10-02. Target: runtime.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### A FluentValidation range rule is published as OpenAPI min/max even under `.When`

**Rule:** a request field that accepts a sentinel outside its range (`-1` = unset) validates with `Must(...)`, not `InclusiveBetween(...).When(...)`: FastEndpoints publishes the range as `minimum`/`maximum` whatever the condition, and the generated zod schema then refuses the sentinel in the browser before the request is sent. Check the regenerated `zod.gen.ts` for the field, and keep the SPA's range check on the bounds the response carries. **Prevents:** a reset the API accepts and the SPA cannot send. **Authority:** `NodeSettingsEndpointValidators` (`IsUnsetOrBetween`), `StoredNodeSettings.TokenSettingUnset`; model-matrix follow-ups V3, 2026-10-05. Target: frontend-and-api.md.

### A wrapper that appears when a field becomes non-empty remounts the input

**Rule:** a Mantine `inputContainer` (or any conditional wrapper around an input) is rendered in the same shape whether the field is empty or set; only its extra children are conditional. Test it by rerendering from blank to set and asserting the input is the same DOM node; a `fireEvent.change` test cannot see a remount. **Prevents:** the input losing focus after the first keystroke, so typing `1500` into an unset field leaves `1` (found only in the browser). **Authority:** `NodeSettingsNumberField`, `NodeSettingsFieldsCard.test.tsx` ("keeps the same input mounted ..."); model-matrix follow-ups, 2026-10-05. Target: frontend-and-api.md.

### Count a tool schema as the chat template renders it, and measure tool cost as a prompt-token difference

**Rule:** llama-server parses the request, so the body's JSON whitespace never reaches the tokens; the templates measured render a schema single-line with `", "` / `": "` separators. Budget tools through `TokenEstimatorCalibrationStore.RenderToolSchema`, the same helper the calibration probe uses, and judge any change by `usage.prompt_tokens` with and without the tools on at least two template families. **Prevents:** counting the indented source text, which under-counted an uncalibrated Qwen turn and over-charged a calibrated one by 150 to 200 tokens at a 4,096 window. **Authority:** `TokenEstimatorCalibrationStore` (`RenderToolSchema`, `DefaultToolTemplatePreambleTokens`); model-matrix follow-ups B1, 2026-10-05 (pinned pair: 769 / 683 / 569 real tokens on Qwen3.5, Granite 4.1, LFM2.5). Target: inference-runtime.md.

### The backend gate empties `.tmp/backend-test-results/` when it starts

**Rule:** never redirect a gate's own output into `.tmp/backend-test-results/`; read `gate.log` there (and the per-project `gate.log` beneath it) and take the exit code from the shell. **Prevents:** a wrapper log that is unlinked seconds after the run starts, leaving only an exit code and no failing test name. **Authority:** `scripts/run-backend-tests.sh`; model-matrix follow-ups, 2026-10-05. Target: backend-tests.md.

### "Idle" by lease is not idle right after a turn: a background request holds the model for seconds

**Rule:** a rule that acts on a chat process having no inference lease (unload it, refuse around it) treats "leased" as "wait, bounded", not as "unavailable": memory extraction and the other post-turn jobs take a lease on the model the moment the user's turn ends. Live-check any such rule with a turn on model A followed at once by a turn on model B. **Prevents:** the ordinary "switch model right after a turn" being refused with "Eject one of them" because the first model was busy for a few seconds (seen live, 2026-10-05). **Authority:** `PooledRoleLaunchAdmission.AdmitChatAsync` (`BusyResidentWaitCap`), `IdleChatEvictionResult`; model-matrix follow-ups W1. Target: models-and-inference.md.
