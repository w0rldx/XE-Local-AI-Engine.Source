# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### A substituted trust resolver answers null grants (target: backend-tests.md)

**Rule:** an `IModelTrustResolver` built with `Substitute.For<T>()` returns null from `ResolveCloudGrantsAsync` and `ClassifyCloudGrants` unless configured. Any test whose trust answer is not `Local` must stub both to `ExternalProviderCloudGrants.None` (or use `FakeModelTrustResolver`, which mirrors the real rule), or the gate under test dies with a `NullReferenceException` instead of withholding. **Prevents:** a cloud-path test that crashes before it reaches the gate it pins. **Authority:** `GraphWorkflowAgentHostFixture`, `GraphWorkflowResponseSchemaWarningTests.BuildService`, `ModelTrustResolverTests.FakeModelTrustResolver_AnswersGrantsLikeTheRealResolver`.

### The host reaches shared provider rules through Client.Application, never OpenAICompatible.Core (target: build-and-analyzers.md)

**Rule:** `XE-Local-AI-Engine.Client` does not reference `Providers.OpenAICompatible.Core`; an endpoint or validator that needs a shared rule (header names, base-address normalization) calls the `Client.Application` surface that wraps it (`StoredExternalProviderHeader.FindViolations`, `OpenAICompatibleBaseAddress` via the store), never the Core type directly. **Prevents:** a host project reference to the leaf transport layer, which `LayerDependencyTests` rejects after the endpoint is already written. **Authority:** `LayerDependencyTests`; `Client.Application` csproj references.

### Regenerating the committed OpenAPI document needs the live host (target: frontend-and-api.md)

**Rule:** `pnpm run openapi:generate` only re-emits the hey-api client from the `openapi/v1.json` already on disk; it never refreshes `v1.json` itself. After a DTO or endpoint change run `OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh` (regenerate mode) and commit both the document and the client; the default mode only compares. **Prevents:** a "regenerated" client that still carries the old contract, caught only by `openapi:check` in CI. **Authority:** `scripts/openapi-live-check.sh` header; `XE-Local-AI-Engine.Client.React/package.json` `openapi:*` scripts.

### A test-support executable must not name its entry type `Program` (target: backend-tests.md)

**Rule:** an executable under a `Testing.*` project names its entry class after the project (`FakeOpenAiGatewayHost`), never `Program`: the test-category scan covers the `Testing.*` projects and derives its integration markers from untagged top-level types by simple name, so a second `Program` either makes the name ambiguous (never a marker) or, when it uses a hosting primitive, becomes a marker itself, and every test that names the node's `Program` is reclassified. **Prevents:** `TestCategoryConventionTests` reclassifying unrelated tests after a fake gains an entry point. **Authority:** `TestCategoryConventionTests.DeriveMarkers`; `FakeOpenAiGatewayHost` remark.
