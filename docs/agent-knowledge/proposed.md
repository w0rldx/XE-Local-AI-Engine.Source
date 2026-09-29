# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Judge listbox/option exposure with a verbose a11y snapshot or a DOM role query, never the default snapshot

**Rule:** the default (non-verbose) accessibility snapshot of the browser tooling drops non-focusable descendants of a control, so every `role="listbox"` using the aria-activedescendant pattern (Mantine `Select`/`Autocomplete` or hand-written) shows as an empty listbox while the DOM, `aria-controls`/`aria-activedescendant` and Chrome's real AX tree are correct. Check a verbose snapshot or `querySelectorAll('[role=option]')` before filing an a11y finding. **Prevents:** chasing a phantom "empty listbox" (Mantine `withScrollArea: false` does not change it and regresses the dropdown height). **Authority:** open-items O3/S4 control test (hand-written listbox, focusable vs non-focusable option), 2026-09-28. Target: frontend topic file.

### Measure chat-template token overhead through llama-server, never with a hand-tuned per-template constant

**Rule:** the calibration round measures the tool preamble per model through `POST /v1/messages/count_tokens` (system + messages + tools; on b10201 it equals `/tokenize` over `/apply-template`), with and without a fixed probe tool set, and subtracts what the estimator already charges for the probe. **Prevents:** a constant tuned on one template (Qwen3.8: ~198) under- or over-charging every other template (qwen2.5-0.5b: 84). **Authority:** `LlamaTokenEstimatorCalibrationService`, `TokenEstimatorCalibrationStore.ResolveToolTemplatePreamble`; open-items O1, 2026-09-28. Target: inference-runtime.md.

### A tamper test on an acquisition sidecar must keep it self-consistent, or the registry repairs it first

**Rule:** `GgufModelRegistry.ListAllAsync` rewrites a shape-invalid or revision-inconsistent sidecar from the manifest before `InstalledGgufSnapshotStore` sees it, so a test that changes only the recorded digest never reaches the store's check; recompute `WeightMemberFingerprint`, `RegistrySourceRevision` and `RegistryRevision` when tampering. **Prevents:** a green "rejects a wrong sidecar" test that never exercised the rejection. **Authority:** `InstalledGgufSnapshotStoreTests` (SeedAcquiredAsync); open-items O2, 2026-09-28. Target: backend-tests.md.

### A supervisor caller that must not extend a helper-loaded model's lifetime passes the Transient intent

**Rule:** a llama.cpp caller that loads a model only for a short helper call sets `ResidencyIntent = ModelResidencyIntent.Transient` on its `LocalModelSelection` (or calls the intent overloads of `EnsureRunningAsync`/`TryAcquireInferenceLease`). Every caller that passes no intent counts as Interactive and clears the transient mark for good, and only a process the Transient request spawned is ever marked. **Prevents:** a helper-loaded model holding memory for the full idle TTL and a cap slot, or, the other way, a new background caller silently giving a draft-loaded model the interactive lifetime. **Authority:** `LlamaServerProcessSupervisor` (`RunningProcess.MarkUsed`, `JoinInflightSpawn`), `LlamaServerIdleReaper.IdleTimeToLiveFor`, `SupervisorTransientResidencyTests`; wiki 03 "Eviction & reaper", 2026-09-29. Target: inference-runtime.md.

### A polling UI reads `ILiveMemorySampler`, never a forced hardware-profile refresh

**Rule:** anything that polls memory for display reads `ILiveMemorySampler` (`GET model-fit/resources`), never `IHardwareProfiler.GetProfileAsync(forceRefresh: true)` or `model-fit/hardware-profile?refresh=true`. **Prevents:** a forced refresh per poll spawns a full probe each time and overwrites `HardwareProfiler`'s process-lifetime cache, the baseline model fit and profile invalidation read, with a transient reading. **Authority:** `LiveMemorySampler` (never touches the profiler cache, one shared probe per ~2 s window), `HardwareProfiler.GetProfileAsync`, `LiveMemorySamplerTests`; wiki 03 "Live memory sampler", 2026-09-29. Target: models-and-inference.md.

### A UI that polls image or whisper residency reads `model-fit/runtime-residents`, never a runtime status route or `isBusy`

**Rule:** poll `GET model-fit/runtime-residents` for image and whisper residents (llama.cpp stays on `model-fit/running`), never `GET transcription/runtime` or `GET images/runtime`, and never read an activity snapshot's `isBusy` as "work is running" or "eject allowed": it counts a resident daemon, while the row's `canEject` repeats the gate's eviction-reservation refusal. **Prevents:** a per-poll settings load, installed-runtime read and model recommendation from the transcription status route; an eject button disabled whenever anything is loaded. **Authority:** `RuntimeResidentsService`, `TranscriptionRuntimeService.GetRuntimeAsync`, `ImageRuntimeActivitySnapshot.IsBusy`, `TryAcquireEvictionReservation` on both activity gates; wiki 14 and 24, 2026-09-29. Target: frontend-and-api.md.
