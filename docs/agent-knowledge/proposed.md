# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### hey-api `validator: true` validates requests too, and a `format: binary` body fails as a string

**Rule:** keep `@hey-api/sdk` at `validator: { request: false, response: true }` in `OpenapiTs.config.ts`. Since
hey-api 0.77 `validator: true` runs the zod schema on the request as well, and the zod plugin types `format: binary`
as `z.string()`, so every multipart `File` body (skill zip preview, chat and image uploads, knowledge-base documents,
transcription) is refused in the browser before any request leaves, with the misleading message "The server
returned a response in an unexpected shape" and nothing in the network panel. **Prevents:** a regenerate that
silently breaks every upload path while every vitest test (msw never sees the request) stays green. **Authority:**
`OpenapiTs.config.ts`; `scripts/openapi-check` drift (the regenerated SDK carries no request validators).
Proposed for: frontend-and-api.md, after "OpenAPI → hey-api is the sole REST data layer for React".

### A substituted trust resolver answers null grants (target: backend-tests.md)

**Rule:** an `IModelTrustResolver` built with `Substitute.For<T>()` returns null from `ResolveCloudGrantsAsync` and `ClassifyCloudGrants` unless configured. Any test whose trust answer is not `Local` must stub both to `ExternalProviderCloudGrants.None` (or use `FakeModelTrustResolver`, which mirrors the real rule), or the gate under test dies with a `NullReferenceException` instead of withholding. **Prevents:** a cloud-path test that crashes before it reaches the gate it pins. **Authority:** `GraphWorkflowAgentHostFixture`, `GraphWorkflowResponseSchemaWarningTests.BuildService`, `ModelTrustResolverTests.FakeModelTrustResolver_AnswersGrantsLikeTheRealResolver`.

### Monaco `editor.api` ships no contributions: an option for one draws nothing until it is imported

**Rule:** `MonacoRuntime.ts` assembles `monaco-editor/editor/editor.api.js` (core only). Any editor feature that is
a contribution (unicode highlighter, hover, find, folding, …) must be imported explicitly from
`monaco-editor/editor/contrib/**`; otherwise its options (`unicodeHighlight`, `renderControlCharacters`, …) are
accepted and render nothing, with no warning. Check on the Vite origin with
`monaco.editor.getEditors()[0].getModel().getAllDecorations()`. Each contribution lands in the lazy editor chunk
(`lazyEditorJavaScriptBytes`); the hover alone is about 310 kB. **Prevents:** an "inspect" mode that passes its unit
tests (they assert the options reach `monaco.editor.create`) yet shows an operator nothing. **Authority:**
`MonacoRuntime.ts`; `CodeEditor.test.tsx`; `config/bundle-budget.json`.
Proposed for: frontend-and-api.md, amending "Client conventions".

### A removed async hop in the generated SDK moves a request from after teardown into the test

**Rule:** amendment to "An MSW request no handler declared FAILS the test that made it": when the generated client
loses a step (the request validators were dropped on 2026-10-09), GETs that used to fire one tick after the test
unmounted now fire inside it and hit the strict msw guard. Register the route in that test; do not widen
`onUnhandledRequest` and do not add a wait. **Prevents:** a red that looks like a flake after an unrelated
hey-api config change (`DevWorkflowDefinitionFormPanel.test.tsx` empty-panel test). **Authority:**
`src/test/UseMswServer.ts`; `DevWorkflowDefinitionFormPanel.test.tsx`.
