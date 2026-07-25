# Keep Model Warm — Test Plan

## Phase 1: storage, runtime settings, and API boundary

| Requirement | Planned evidence |
|---|---|
| Old-file compatibility and off default | `OldFileMissingNewFields_LoadsToDefaults_WithoutThrowing`; `KeepModelWarm_StoredAbsent_UsesOffByDefaultValues` |
| Trim model and preserve valid interval | Extend `StoredFields_WithinRange_RoundTrip`; add `KeepModelWarmModelName_Blank_FallsBackToNull` |
| Clamp invalid interval independently | `KeepModelWarmIntervalSeconds_OutOfRange_FallsBackToNull` |
| Fresh live runtime values | `KeepModelWarm_AsyncGetters_ReadStoreFreshOnEveryCall` |
| Endpoint round-trip and bounds | Extend `SaveNodeSettings_WithNewMigratedFields_RoundTripsThroughGet` |
| Explicit false and null-preserving merge | Extend `SaveNodeSettings_WhenOmittingOptionalFields_KeepsCurrentStoredValues`; add disabled case if needed |
| Reject invalid interval/model combination | `SaveNodeSettings_WhenKeepModelWarmIntervalOutOfRange_ReturnsValidationProblem`; `SaveNodeSettings_WhenKeepModelWarmEnabledWithoutModel_ReturnsValidationProblem` |

## Phase 2: background service

New file: `XE-Local-AI-Engine.Tests/BackgroundServices/KeepModelWarmBackgroundServiceTests.cs`.

| Requirement | Planned evidence |
|---|---|
| Disabled is a no-op | `RunIterationAsync_WhenDisabled_DoesNotWarmModel` |
| Enabled warms selected model | `RunIterationAsync_WhenEnabled_WarmsConfiguredModel` |
| Toggle/model changes are live | `RunIterationAsync_WhenSettingsChange_UsesNewModelWithoutRestart` |
| Interval changes are live | `RunIterationAsync_WhenIntervalChanges_UsesNewCadenceWithoutRestart` |
| Resident model is touched, not respawned | `RunIterationAsync_WhenIntervalElapses_TouchesModelAgain`; existing supervisor reuse test plus a refreshed-TTL regression if needed |
| Failures do not terminate future iterations | `RunIterationAsync_WhenWarmFails_LogsAndRetriesAfterInterval` |
| Cancellation propagates cleanly | `RunIterationAsync_WhenWarmIsCancelled_PropagatesCancellation` |

## Phase 3: frontend

Targets:

- `NodeSettingsFieldsModel.test.ts`
- `NodeSettingsFieldsCard.test.tsx`
- `NodeSettings.test.tsx` where generated-mutation integration needs coverage.

| Requirement | Planned evidence |
|---|---|
| Map/default/bounds | Model tests for absent/default fields and server bounds |
| Validate/build PUT body | Model tests for enable/model/interval, explicit false, missing model, and out-of-range interval |
| Render and edit controls | Card tests for toggle/model/interval and disabled state |
| Surface caveats | Card copy assertions for one loaded-process slot, VRAM occupation, and interval below TTL |
| Generated contract | `XE_LAUNCH_MODE=desktop pnpm run openapi` followed by `pnpm openapi:check` |

## Validation sequence

1. Targeted frontend Vitest files.
2. Targeted backend build, then guarded filtered tests.
3. Desktop-mode OpenAPI generation/check.
4. Frontend lint, test, and build.
5. Full backend restore, Release build, and serial test suite.
6. Aspire isolated start, resource wait, authenticated browser smoke, logs/traces review.
