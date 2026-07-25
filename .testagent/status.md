# Keep-Model-Warm Test Status

## Requirement evidence

| Requirement | Evidence |
| --- | --- |
| Off-by-default, persisted nullable settings | `OldFileMissingNewFields_LoadsToDefaults_WithoutThrowing`; `StoredAbsent_UsesAppsettingsSeed`; `StoredAndSeedAbsent_UsesHardcodedDefault` |
| Model trimming and interval normalization | `StoredFields_WithinRange_RoundTrip`; `KeepModelWarmIntervalSeconds_OutOfRange_FallsBackToNull`; `KeepModelWarmModelName_Blank_FallsBackToNull` |
| Partial-update API semantics, explicit false, and validation | `SaveNodeSettings_WhenOmittingOptionalFields_KeepsCurrentStoredValues`; `NodeSettings_KeepModelWarmFields_RoundTripThroughMapper`; `SaveNodeSettings_WhenKeepModelWarmIntervalOutOfRange_ReturnsValidationProblem`; `SaveNodeSettings_WhenKeepModelWarmEnabledWithoutModel_ReturnsValidationProblem` |
| Live enable/disable/model/interval changes without restart | `RunIterationAsync_WhenEnabled_WarmsConfiguredModel`; `RunIterationAsync_WhenDisabledAfterWarm_StopsTouchingWithoutRestart`; `RunIterationAsync_WhenSettingsChange_UsesNewModelWithoutRestart`; `RunIterationAsync_WhenIntervalChanges_UsesNewCadenceWithoutRestart` |
| Disabled state and invalid incomplete state do not warm | `RunIterationAsync_WhenDisabled_DoesNotWarmModel`; `RunIterationAsync_WhenEnabledWithoutModel_DoesNotResolveProvider` |
| Resident model is touched again and warm failures recover | `RunIterationAsync_WhenIntervalElapses_TouchesResidentModelAgain`; `RunIterationAsync_WhenWarmFails_RetriesAfterConfiguredInterval` |
| Host cancellation stops the timer loop cleanly | `ExecuteAsync_WhenHostStops_CancelsPendingTimerCleanly` |
| Supervisor reuse refreshes idle TTL without a second spawn | `EnsureRunning_ReusedBeforeIdleTtl_RefreshesLastUsedAndPreventsEviction` |
| Frontend mapping, payload, validation, and explicit clear/false | `enables keep-warm with a trimmed model and bounded interval`; `requires a selected model when keep-warm is enabled`; `emits explicit false and no unchanged keep-warm fields when disabling`; `lets disabling win over an invalid interval draft`; `uses an empty string to explicitly clear the selected keep-warm model while disabled`; `rejects an out-of-range keep-warm interval` |
| Toggle, disabled controls, picker filtering, and caveats | `renders the live toggle and disables the model and interval controls while off`; `edits the toggle, llama.cpp model, and interval through the generic onChange`; `offers only installed llama.cpp chat models in the keep-warm picker`; `surfaces the VRAM, MaxLoadedProcesses slot, and idle-TTL caveats` |

## Fresh validation

- Focused backend suite: 79/79 passed with the assembly contamination guard.
- Backend Release build: succeeded with 0 warnings and 0 errors.
- Full guarded backend suite: 4,278 passed, 3 expected opt-in integration tests skipped, 0 failed; assembly output unchanged.
- Focused frontend suite: 60/60 passed.
- Full frontend suite: 201 files, 1,619 tests passed.
- Frontend lint/typecheck and production build: passed.
- Desktop OpenAPI snapshot/client generation: passed from an Aspire-managed application endpoint.
- Aspire + Chrome live validation:
  - default toggle rendered off with model/interval controls disabled;
  - installed llama.cpp chat model was selectable;
  - enabling saved `true`, the selected model, and a 60-second interval;
  - Aspire logs showed one `llama-server` spawn and ready event immediately after the live save;
  - disabling saved explicit `false` without clearing the selected model or interval.

## Assertion-quality / gap review

- Assertions cover state, exact API payloads, normalization boundaries, process launch count, last-used TTL refresh, retry cadence, and UI accessibility/disabled behavior.
- Time-dependent service tests use a manual `TimeProvider`; no arbitrary short sleeps are used for correctness assertions.
- Provider-resolution failures for stale/manually edited model names are covered by the service retry test rather than by an endpoint existence check; the UI restricts normal selection to installed llama.cpp chat models.
- Full E2E Playwright was not run because the repository marks it ask-gated; the requested behavior was validated through Aspire and Chrome DevTools instead.
