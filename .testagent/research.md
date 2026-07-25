# Keep Model Warm — Test Research

## Target inventory

- `XE-Local-AI-Engine.Client.Application/Services/NodeSettings/StoredNodeSettings.cs`
- `XE-Local-AI-Engine.Client.Application/Services/NodeSettings/Implementation/NodeSettingsStore.cs`
- `XE-Local-AI-Engine.Client.Application/Services/NodeSettings/INodeRuntimeSettings.cs`
- `XE-Local-AI-Engine.Client.Application/Services/NodeSettings/Implementation/NodeRuntimeSettings.cs`
- `XE-Local-AI-Engine.Client/Endpoints/NodeSettings/V1/`
- `XE-Local-AI-Engine.Client/BackgroundServices/KeepModelWarmBackgroundService.cs` (new)
- `XE-Local-AI-Engine.Client/ConfigureServices.cs`
- `XE-Local-AI-Engine.Client.React/src/features/node-settings/`
- Generated OpenAPI snapshot/client under `XE-Local-AI-Engine.Client.React/openapi/` and `src/core/api/generated/`

## Existing conventions

- Backend tests use TUnit on Microsoft.Testing.Platform, `[Test]` / `[Arguments]`, `AssertEx`, and NSubstitute.
- Node settings use nullable stored fields, per-field normalization, and stored > seed > hardcoded-default accessors.
- Optional endpoint request fields merge into current settings; explicit `false` must not be treated as omission.
- Frontend tests use Vitest and Testing Library. Generated API files are regenerated, never hand-edited.
- Backend build and test processes must run sequentially under the repository build/assembly guards.

## Architecture findings

- llama.cpp residency is refreshed only when `WarmModelAsync` reaches `EnsureRunningAsync` and the reuse path calls `MarkUsed`.
- A literal “already resident, do nothing” branch would defeat keep-warm and permit idle eviction.
- `GetRuntimeInfo` is not a residency predicate: a live process can return null when `/props` has no usable context.
- The background service must therefore call the idempotent provider warm operation each due interval. Existing supervisor reuse prevents a second spawn.
- Live settings require a fresh runtime-settings read on every poll. A fixed five-second poll supports live enable/disable/model changes while the user interval controls only the warm cadence.
- Selected models route through `ILocalModelProviderResolver`, preserving the repository’s model-to-provider mapping.

## Acceptance checklist

- [x] New fields are nullable in storage and old files remain compatible.
- [x] Missing enabled flag resolves to off.
- [x] Model names are trimmed; blank names normalize to null.
- [x] Interval defaults to 300 seconds and is bounded to 5–3600 seconds.
- [x] GET/PUT round-trip all fields and interval bounds; explicit false persists.
- [x] Enabling without a selected model is rejected.
- [x] Background service observes enable, disable, model, and interval changes without restart.
- [x] Every due enabled interval touches `WarmModelAsync`; no second llama-server spawn occurs on reuse.
- [x] Failures are logged and do not terminate the background loop.
- [x] React renders toggle/model/interval controls and the VRAM/slot plus interval-vs-TTL caveats.
- [x] React request construction validates and sends the new fields.
- [x] Desktop-mode OpenAPI generation commits the regenerated client.
