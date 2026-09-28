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
