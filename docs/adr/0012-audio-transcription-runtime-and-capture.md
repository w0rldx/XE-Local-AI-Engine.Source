# ADR 0012: Local audio transcription runs on a supervised whisper.cpp daemon, captures in the browser, and never persists audio

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-11, as operator decisions D1–D4.
- **Date:** 2026-09-12
- **Scope:** How the product transcribes audio on the node: which runtime, where audio is captured, how speakers are
  attributed, how a Linux CUDA binary is obtained, the order the feature is built in, and the one rule that outranks
  all of them — audio is never written to the database. It changes nothing about the chat, image or training runtimes.
- **Authority:** Operator decisions D1–D4, taken 2026-09-11 on the assessment of the "Local Audio Transcription and
  Voice Input" brief, plus the reconciliation rounds that closed it.
- **Amends:** nothing.

## Context

The product is asked for local speech-to-text: transcribe an uploaded recording, transcribe a live meeting, label who
said what, and eventually dictate into the agent chat — all on the user's own machine, with no cloud provider.

Two of those asks were under-specified and one was factually wrong, which is what this record exists to settle.

**The runtime half was already a solved shape.** The node supervises `llama-server` for text and `sd-server` for
images: pinned, hash-verified binary acquisition, one loopback port range per runtime, OS-specific tree-kill teardown,
a startup stale-daemon reaper, an idle-TTL reaper, and a shared GPU load-admission gate. whisper.cpp ships the same
kind of artifact — a small HTTP daemon over a ggml model — so a third copy of that pattern was the low-risk answer,
and a live spike confirmed it end to end in an afternoon.

**The capture half was not.** The brainstorm proposed native OS capture: NAudio/WASAPI on Windows, PipeWire on Linux.
The SPA is the only part of this product that already touches audio, no server-side capture seam exists, and there is
no maintained .NET PipeWire binding, so native-first would have meant two new platform stacks and two UX paths before
the first transcript appeared.

**"Speaker 1 / Speaker 2" does not exist in whisper.cpp.** Nothing in it clusters unknown speakers from a mono mix.
`--diarize` is a stereo channel split and `-tdrz` is an English-only experimental turn marker needing its own model.
Shipping a "diarization" label over either would be a claim the engine cannot honour.

**Linux NVIDIA has no prebuilt.** whisper.cpp publishes release binaries only on its nightly `b<n>` tags, and the
Ubuntu asset is CPU-only — the same gap llama.cpp and stable-diffusion.cpp have, which this repository already answers
with a managed source build plus a bring-your-own override.

**And audio is the most sensitive payload this product has yet handled.** A recording of a meeting is not a prompt: it
carries voices of people who never used the product. The engine stores chat content encrypted at rest; for audio the
answer is to not store it at all.

## Decision

1. **whisper.cpp is a third independent supervised runtime** (`Providers.WhisperCpp`), not an in-process library. It
   publishes `IWhisperTranscriber` and `IWhisperServerSupervisor` and nothing else: no `whisper-server` flag, route,
   multipart field or JSON shape escapes the project. It implements neither `ILocalModelProvider` nor `IChatClient`.
   No supervisor, no `CapacityService` and no shared base class is refactored to accommodate it — it copies the
   pattern rather than extracting one. Whisper takes the shared `IGpuModelLoadAdmission` gate for a spawn and for an
   in-place model switch, and stays out of the byte ledger.

2. **D1 — Capture is browser-first hybrid.** V1 captures the microphone (`getUserMedia`) and system audio
   (`getDisplayMedia({audio})`) in the SPA on both operating systems, plus file upload, transported as raw 16 kHz mono
   PCM frames. Windows per-application capture (NAudio 3.1.0 process loopback) is a later slice and the only thing
   native capture is adopted for; Linux per-application capture is deferred beyond this plan. No `MediaRecorder`, no
   compressed audio in the browser path.

3. **D2 — Speakers are attributed by channel, never clustered.** When a session captures two sources, each channel is
   transcribed separately and its segments are labelled **You** (microphone) or **Others** (system audio).
   `TranscriptChannel` carries exactly `Mono`, `You` and `Others`. No Speaker 1/2/n, no tinydiarize, no speaker
   embeddings, and no UI string that implies otherwise.

4. **D3 — The Linux CUDA lane is a managed source build.** It mirrors the llama.cpp and stable-diffusion.cpp lane:
   explicit fetch of a peeled commit SHA, an isolated build environment, a smoke test, and an installed-runtime record
   that fails closed on drift. The pinned CPU prebuilt is the floor and `XE_WHISPERCPP_SERVER_PATH` is the
   bring-your-own override.

5. **D4 — The build order is runtime → batch upload → live → later slices.** Runtime and catalogue first; then the
   session entity, the batch file path and the SPA area; then the live segmenter and its hub; then the live capture
   UI; then Windows per-process capture; dictation into the agent chat last. The live path gets its own slice because
   a fixed-window loop against the stateless inference route hallucinated on a mid-word cut and duplicated words at
   every overlap boundary — cut points must come from voice activity detection and a commit/stitch layer, which is a
   design problem, not a knob.

6. **Audio is never persisted.** No audio column, no blob store, no recording archive, no "download the audio". An
   upload is streamed into one engine-owned temporary file, handed to the runtime, and deleted by the single object
   that owns it; live PCM exists only in the segmenter's in-memory buffer. The rule is enforced, not asserted: the
   entities have no byte-payload member, an architecture test sweeps their members against an allow-list, the upload
   slot is the sole owner of every path it mints and its disposal is the only deleter, and the upload endpoint streams
   the multipart section itself so the web framework never buffers a plaintext copy of its own.

## Evidence

- **The runtime was proven live before it was planned** (`spike/whisper-cpp-spike-report.md`, 2026-09-11, on this
  maintainer's box): whisper.cpp `v1.9.4` built with CUDA in about three minutes; `whisper-server` served multipart
  inference requests returning timed segments, a detected language and language probabilities; `POST /load` swapped
  models in place; SIGTERM left no orphan. Round trips were 0.09–0.13 s for 2–10 s chunks on `large-v3-turbo-q8_0`,
  which is what makes the batch path feel synchronous. Server-side voice activity detection with the Silero model cut
  55.8 % of samples from a silence-padded clip.
- **Readiness, corrected.** The spike's §2/§6 claim that the server has no `/health` route is **wrong**, and its §8
  erratum records the correction: at the pinned commit `927cfce34f31707e17f2bff35c349632fb9e2c3a` (the peeled commit
  behind the annotated `v1.9.4` tag, and the revision the managed build checks out) the server registers `GET /health`,
  returning 503 while a model loads and 200 once it is ready. Readiness polls that route; the stdout "listening" line
  is fully buffered off a TTY and was observed absent while the port was already live.
- **The live-window failure is quoted, not inferred.** A 2 s no-overlap chunk loop produced
  `"What you're coming to do is not what you're coming to do."` from a mid-word cut; 0.5 s of overlap repeated one to
  three words at every boundary, because the server holds no state between requests.
- **Diarization.** Read from the upstream server source and its documentation: `--diarize` splits stereo channels and
  `-tdrz` is an English-only experimental marker with a separate model. Neither is speaker clustering.
- **Licences** (`spike/audio-capture-research.md` §6, `spike/whisper-cpp-spike-report.md` §2 row 5b): whisper.cpp MIT
  (`LICENSE` in the cloned repository), Whisper weights MIT upstream with the ggml conversion inheriting it, Silero
  VAD MIT, NAudio MIT. Registered in `NOTICE` §3 and §5.
- **Delivered and gated.** The runtime slice landed as `f3ee1689d` with the supervisor, catalogue, managed CUDA lane
  and its own live round (`progress/S1a-live-round.md`, `progress/S1b-live-round.md`). The session slice's persistence
  and service commits are covered by `AddTranscriptionSessionsMigrationTests`, `TranscriptionSessionStoreTests`,
  `TranscriptionNoAudioPersistenceTests` (the no-audio fence), `TranscriptionServiceTests`,
  `TranscriptionUploadSlotTests`, `AudioContainerSnifferTests` and `FfmpegAudioTranscoderTests`. The upload-slot
  guarantee was verified by negative control: stubbing the delete loop out of `TranscriptionUploadSlot.DisposeAsync`
  turns eleven of those tests red.

## Consequences

Stated honestly, including the ones that are costs.

- **Browser capture shows a screen-share picker every session**, and `systemAudio: "include"` is a hint the operating
  system may ignore. The UI must verify the returned stream actually carries an audio track and say so when it does
  not. That is a UX cost accepted in exchange for one capture path on two operating systems and no new native
  dependency.
- **Per-application capture is a Windows-only promise.** Linux users get microphone and whole-system audio and nothing
  finer, for as long as no maintained .NET PipeWire binding exists. As built (D1's native-capture slice), it costs
  **no new project and no Windows target framework**: the leaf `NAudio.Wasapi` package sits on `Client.Application`
  and is kept honest by `[SupportedOSPlatform]` attributes plus an `OperatingSystem.IsWindows()` branch at the single
  DI call site, never a CA1416 suppression. A `Providers.*` project was rejected because such a project may reference
  only `Providers.Abstractions` and so could not reach the live-session registry the capture pump exists to feed.
- **One capture scope, and it is the process tree.** `ProcessLoopbackMode` offers `IncludeTargetProcessTree` or its
  complement, and the complement records everything *except* the target — so "this application only" is not a mode
  that exists. The product captures the target **and its descendants**, the user-facing copy says so, and excluding a
  target's children is an unresolved limitation rather than an approximated feature.
- **The Windows floor is the conservative one.** `ProcessAudioCaptureSupport.MinimumWindowsBuild` is **20348**, the
  build Microsoft documents for `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`, not the 19041 NAudio annotates for the
  analyzer. Being wrong this way hides a capability on builds 19041–20347; being wrong the other way is a hard COM
  failure. Lowering it needs a live observation on such a build, not a code review.
- **"You / Others" is weaker than diarization, and honest.** A single microphone recording two people in one room
  stays one channel, and the product must not pretend otherwise. Turn markers remain a documented later option.
- **Whisper's VRAM is unaccounted in the byte ledger**, exactly as the image runtime's is. Its footprint is small
  enough — roughly 0.8–1.8 GB depending on tier — that this is safe on any box that can run a local chat model. The
  gap is recorded, not fixed; widening the ledger is an engine-wide change with its own decision.
- **Linux NVIDIA users must build or bring their own binary** for GPU transcription, and the CPU floor is what an
  untouched install gets. This is the same posture the other two runtimes already have.
- **Error detail is encrypted, so it cannot be queried.** `error_code` and `error_message` are an encrypted pair, so
  no SQL filter or aggregate can read them; the list surface filters on the plaintext `status` instead.
- **The never-persist rule constrains every later slice.** Features that would otherwise be natural — replaying a
  recording beside its transcript, re-transcribing with a better model, exporting the audio — are unavailable by
  construction, and adding any of them means revisiting this record rather than adding a column.
- **Revisiting is expected.** Linux per-application capture, tinydiarize turn markers, a widened VRAM ledger and a
  non-browser capture default are each a new operator decision, not an edit to this record.
