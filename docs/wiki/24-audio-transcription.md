# Audio Transcription (whisper.cpp)

> Reviewed: 2026-09-15 · Code-grounded.

The node transcribes audio **locally** with [whisper.cpp](https://github.com/ggml-org/whisper.cpp), supervised as a
`whisper-server` child process exactly the way `llama-server` and `sd-server` are. A **transcription session** is a
persisted, encrypted row; its transcript is a list of segments beneath it. The audio itself is **never persisted** —
an upload lives in one engine-owned temporary file for the length of one transcription and is deleted with the object
that minted it.

This page covers what exists today: the runtime and its model catalogue (delivered first, summarized here and
documented in full on [Local Runtime & Providers](03-local-runtime-and-providers.md#providerswhispercpp--the-supervised-speech-to-text-runtime)),
the session/segment data model, the **batch file-upload** transcription path with its endpoints and React feature,
the **live** transcription pipeline (live-start endpoint, `TranscriptionHub` and the segmenter — see
[Live sessions](#live-sessions)), the **browser capture** that feeds it (see
[Browser capture and the live UI](#browser-capture-and-the-live-ui)), and **Windows per-application capture**, the one
server-side capture source (see [Windows per-application capture](#windows-per-application-capture)). Dictation is
**not built yet** — see [What is not here yet](#what-is-not-here-yet).

The decisions behind the feature — browser-first capture, channel attribution instead of diarization, the managed
Linux CUDA source build, the slice order, and the never-persist-audio rule — are recorded in
[ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md).

## Where the code lives

| Concern | Project / path |
|---|---|
| Runtime supervision, transcriber, argument builder, pins, catalogue | `XE-Local-AI-Engine.Providers.WhisperCpp/` (`IWhisperServerSupervisor`, `IWhisperTranscriber`, `WhisperCppReleasePins`, `WhisperModelCatalog`) |
| Runtime/model application services | `XE-Local-AI-Engine.Client.Application/Services/Transcription/` (`ITranscriptionRuntimeService`, `IWhisperModelDownloadCoordinator`, `WhisperModelPathResolver`) |
| Runtime endpoints' door onto the provider (endpoint-dependency rule) | `…/Services/Transcription/WhisperRuntimeOrchestrationService.cs` — pass-through over `IWhisperCppSourceBuildService`, `IWhisperCppSourceBuildPrerequisiteProbe`, `IWhisperRuntimeActivityGate` and `IWhisperBackendSelector`; the six source-build/recommendation endpoints inject it, never the provider contracts |
| Session lifecycle + batch transcription | `…/Services/Transcription/Implementation/TranscriptionService.cs` (`ITranscriptionService`) |
| Windows per-application capture | `…/Services/Transcription/Capture/` (`IProcessAudioCaptureSource`, `WindowsProcessAudioCaptureSource`, `NotSupportedProcessAudioCaptureSource`, `ProcessAudioCaptureCoordinator`, `ProcessAudioCaptureSupport`, `ProcessAudioCaptureCandidates`, `Wasapi16kMonoPcmConverter`) |
| Container sniffing and engine-side transcode | `…/Services/Transcription/AudioContainerSniffer.cs`, `…/Implementation/FfmpegAudioTranscoder.cs` (`IAudioTranscoder`) |
| Entities + store | `XE-Local-AI-Engine.Client.Persistence/Entities/{TranscriptionSession,TranscriptSegment,TranscriptionEnums}.cs`, `Stores/ITranscriptionSessionStore.cs`, `Implementation/TranscriptionSessionStore.cs` |
| Options | `…/Services/Transcription/TranscriptionOptions.cs`, `XE-Local-AI-Engine.Providers.WhisperCpp/Options/WhisperRuntimeOptions.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/Transcription/V1/`, routes in `LocalApiRoutes.Transcription` |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/transcription/` |

## The runtime, in brief

`WhisperServerProcessSupervisor` owns **one** resident `whisper-server`: a node has one selected transcription model
and the server serializes every request on a single mutex, so a second daemon could not serve anyone faster. Readiness
is a port accept followed by the server's health route; a 503 there means "loading a model" and keeps the poll going,
which also covers an in-place model switch. GPU loads serialize through the shared `IGpuModelLoadAdmission` gate, and
no `whisper-server` flag, route or JSON shape escapes `Providers.WhisperCpp` — the engine sees `IWhisperTranscriber`
and `IWhisperServerSupervisor` and nothing else.

`WhisperModelCatalog` is a static table of seven Whisper weights plus the pinned Silero VAD file
(`ggml-silero-v6.2.0.bin` from the `ggml-org/whisper-vad` Hugging Face repository). Prebuilt binaries come from the
pinned nightly tag in `WhisperCppReleasePins`; upstream publishes no Linux CUDA asset, so a Linux NVIDIA box resolves
the CPU tarball and the CUDA lane is the managed source build or the `XE_WHISPERCPP_SERVER_PATH` override. Full
detail: [Local Runtime & Providers](03-local-runtime-and-providers.md#providerswhispercpp--the-supervised-speech-to-text-runtime).

## Sessions and transcripts

Two entities, both `internal sealed record class`, both mapped in `Configurations/`:

- **`TranscriptionSession`** (`transcription_sessions`) — lifecycle `Status` (`Created` → `Transcribing` →
  `Completed` / `Failed` / `Cancelled`), `SourceKind`, the resolved `ModelId`, timestamps in unix ms, the detected
  language and audio duration, plus four **encrypted** columns: `Title`, `ConfigJson`, `ErrorCode`, `ErrorMessage`.
- **`TranscriptSegment`** (`transcript_segments`) — `Seq`, `StartMs`, `EndMs`, encrypted `Text`, `Channel` and an
  optional `Confidence`, with a cascade FK to the session and a unique `ux_transcript_segments_session_seq` index on
  `(session_id, seq)` that is what stops a double allocation of a sequence number.

`TranscriptionSourceKind` carries `File`, `Microphone`, `SystemAudio`, `MicrophoneAndSystem`, `Dictation` and
`ApplicationProcess` from day one, and `TranscriptChannel` carries `Mono`, `You` and `Others`, because both are
persisted as ints and adding a member later would be a schema change. `File` (batch upload) and the live capture kinds
`Microphone`, `SystemAudio` and `MicrophoneAndSystem` are creatable from the SPA on every host, and `ApplicationProcess`
on a Windows host that reports `processCaptureSupported`; only `Dictation` is not creatable from any surface yet.
`TranscriptionService.LiveChannelsFor` maps `SystemAudio` **and `ApplicationProcess`** to the `Others` lane and
`MicrophoneAndSystem` to both (`You` and `Others`), which is what lets a Windows per-application capture share the
browser's system-audio lane rather than inventing one; single-source sessions carry `Mono`.

**Encryption** goes through the same column path as chat: `NodeEncryptionSaveChangesInterceptor` encrypts on write and
`NodeEncryptionMaterializationInterceptor` decrypts on materialization, with AAD bound to the column names
`transcription_session_title`, `transcription_session_config_json`, `transcription_session_error_code`,
`transcription_session_error_message` and `transcript_segment_text`. A segment row moved to another session therefore
fails its tag check instead of surfacing as that session's transcript. Because the error pair is encrypted, it is
deliberately **not SQL-queryable**; the list view filters on `status`, which stays plaintext. See
[Data & Persistence](08-data-and-persistence.md).

`ITranscriptionSessionStore` is the only way application code reaches those tables. Three details of its contract
matter to callers: `AppendSegmentsAsync` checks the session exists and returns `false` when it does not (the node
connection leaves `PRAGMA foreign_keys` off, so a database-level cascade cannot be relied on); `DeleteAsync` uses
`ExecuteDeleteAsync` inside one transaction rather than loading and decrypting the whole transcript; and both the
summary and detail views carry `SegmentCount`, while `ErrorCode`/`ErrorMessage` are on the **detail** view only.

## The batch upload path

One `POST transcription/sessions/{sessionId}/file` request carries the audio. What happens, in order:

1. **The upload streams.** The endpoint declares `AllowFileUploads(dontAutoBindFormData: true)` and reads the
   multipart section itself, so no framework-buffered copy of the audio lands in the ASP.NET Core temp directory — a
   bound `IFormFile` spills anything over 64 KB to disk before a handler ever runs.
2. **Into an owned temp slot.** `ITranscriptionService.BeginUploadAsync` mints a `TranscriptionUploadSlot`
   (`IAsyncDisposable`) under `{INodeDataDirectory.Root}/tmp/transcription/`. The file is **server-named**
   (`{Guid:N}{ext}`); the client's file name never reaches a path, only the session title. `CopyFromAsync` enforces
   `SecurityOptions.MaxUploadFileSizeMb` **during** the copy and throws `TranscriptionUploadTooLargeException` at the
   limit rather than after it. One `await using` spans the copy *and* the transcription.
3. **The container is sniffed, never trusted.** `AudioContainerSniffer.Detect` reads 16 leading bytes and matches
   RIFF/WAVE, `fLaC`, `OggS`, `ftyp`, EBML, or an ID3 tag / MPEG frame sync. `wav`, `mp3` and `flac` reach the runtime
   untouched; `ogg`, `m4a` and `webm` are accepted only when this engine can transcode them. Anything else is a typed
   **415** naming the detected container, the list this node supports, and whether `ffmpeg` is the missing piece.
4. **Transcoding is engine-side.** `FfmpegAudioTranscoder.ToWav16kMonoAsync` runs `ffmpeg` into a **second** path
   minted through the same slot, so a converter killed halfway still leaves its partial file under one owner.
   `whisper-server` is never launched with its own audio-conversion flag: that writes every request's bytes to disk,
   including live audio.
5. **One transcriber call per channel.** A file is one `Mono` channel, but `TranscriptionService.RunAsync` loops over
   a channel array so the live slices feed it two without reshaping it. Segments are ordered by start time and
   allocated `Seq` **starting at 1**, never 0, so a subscriber cannot confuse "no segments yet" with "segment zero".
   Fractional provider seconds are rounded to whole milliseconds at that boundary.
6. **The row terminalizes exactly once.** Segments and the completion share one timestamp. A cancel moves the session
   to `Cancelled`; a failure writes an error code — `unsupported-container`, `transcode-failed`, `runtime-failed`,
   `already-transcribing` or the catch-all `transcription-failed` — into the encrypted pair. The catch-all is the
   point rather than a fallback: the row is already `Transcribing` before anything in the run can throw, and nothing
   else ever writes it, so an unhandled exception would strand the session forever.

Cancellation is registered **before** the transcode, not after it, so a cancel arriving during conversion reaches
`ffmpeg`'s child process instead of being refused as "not running". `TranscriptionService` takes no activity lease of
its own — `IWhisperTranscriber` acquires and releases one inside each call, and a nested second lease would either
deadlock or double-count.

## Audio is never persisted

This is the feature's load-bearing privacy rule, and it is enforced in four places rather than asserted once:

1. **No audio column exists.** Neither entity has a byte-payload member of any kind.
2. **`TranscriptionNoAudioPersistenceTests`** (`Client.Persistence.Tests`) sweeps both entities' members, including
   non-public ones, against an allow-list, so adding an audio-bearing property fails the build's test gate.
3. **The slot is the single owner of every path it mints**, `AddOwnedPath` included, and disposal is the only thing
   that deletes them. Nothing in the service deletes a file; a second deleter could only produce a double delete or a
   leak. `TranscriptionUploadSlotTests` covers the mid-stream failure, the over-cap rejection, the cancellation, the
   transcode second path, idempotent disposal and the extension-normalization cases.
4. **The streaming upload keeps the framework out of it** (step 1 above); the endpoint test asserts an isolated
   ASP.NET Core temp directory stays empty on success, rejection, cancellation and handler failure.

Live PCM lives only in the segmenter's in-memory ring buffer — the same rule, one layer up. In the browser it lives
only in the worklet's current frame and the at most two frames in flight to the hub; nothing is written to disk or to
any storage on either side.

## Endpoints

Routes under `transcription/*` (`LocalApiRoutes.Transcription`), one endpoint class per file in
`Endpoints/Transcription/V1/`, every one `Policies(NodeAuthorizationPolicies.Operator)`.

| Endpoint | Route | Role |
|---|---|---|
| `ListTranscriptionSessionsEndpoint` | `GET transcription/sessions` | One page of sessions, newest first, with the unpaged total. `Limit`/`Offset` are clamped in the handler. |
| `CreateTranscriptionSessionEndpoint` | `POST transcription/sessions` | Creates a `Created` session, resolving the effective model when the request names none. |
| `GetTranscriptionSessionEndpoint` | `GET transcription/sessions/{sessionId}` | One session with its decrypted transcript. |
| `DeleteTranscriptionSessionEndpoint` | `DELETE transcription/sessions/{sessionId}` | Cancels anything in flight, then deletes the session and its segments. |
| `CancelTranscriptionSessionEndpoint` | `POST transcription/sessions/{sessionId}/cancel` | Signals the in-flight transcription for this session. |
| `UploadTranscriptionAudioEndpoint` | `POST transcription/sessions/{sessionId}/file` | The streaming multipart upload and the batch transcription it drives. |
| `ListCaptureProcessesEndpoint` | `GET transcription/capture/processes` | The per-application capture picker: `{ supported, processes: [{ pid, name, hasAudio }] }`, one row per non-expired render session, `hasAudio` telling the operator which of them is playing right now. Only the id and the name — no path, window title or command line. |
| `StartProcessCaptureEndpoint` | `POST transcription/sessions/{sessionId}/capture/process` | Attaches server-side capture of one process to a session that is already live. 400 `capture-not-supported`, 409 `session-not-live` / `capture-already-running`. |
| `StopProcessCaptureEndpoint` | `DELETE transcription/sessions/{sessionId}/capture/process` | Stops that capture: 204, or 404 when the session was not capturing. It does **not** end the session. |

The runtime and model-catalogue routes of the same family — `transcription/runtime*`, `transcription/models*` and the
three source-build routes — are listed in [API & Hubs](09-api-and-hubs.md). The whole prefix is gated: with
`Transcription:Enabled` off, a request-path middleware in `Program.cs` answers 404 while the endpoints stay
*discovered*, so the OpenAPI document and the generated client are identical on every node.

## Live sessions

The rolling-buffer commit pipeline sits behind a live-start endpoint and one hub; the browser capture that drives
them is [below](#browser-capture-and-the-live-ui). A live session and a file session are the same
`TranscriptionSession` row taking a different path through `ITranscriptionService` — the batch path above is
unaffected.

### Starting a session live

`POST transcription/sessions/{sessionId}/live/start` (`StartLiveTranscriptionSessionEndpoint`, Operator, idempotent,
no body) reads only the session row and puts it on the live path:

| Outcome | Response |
|---|---|
| `Started` or `AlreadyLive` | 200 with the session's status and `lastSeq` |
| Unknown session | 404 |
| Session already finished | 409 |
| A `File`-kind session | 400 |

The status move to `Transcribing` is a compare-and-set (`ITranscriptionSessionStore.TryTransitionStatusAsync`), so a
start that raced a graceful end refuses with 409 rather than writing `Transcribing` over the terminal status that end
had already recorded — a finished session is never resurrected by a start that arrived a moment too late.

The browser awaits a 200 from this endpoint before it forwards a single frame — a frame that arrives before the
session is armed has nowhere to land.

### `TranscriptionHub`

Route: `/api/local/v1/transcription/hub`.

Client → server:

| Method | Contract |
|---|---|
| `SubscribeSession(sessionId, afterSeq)` | Joins the session's group, then replays committed segments after `afterSeq` as a snapshot `{ sessionId, status, lastSeq, segments, replayTruncated }`. Join happens before replay. |
| `UnsubscribeSession(sessionId)` | Leaves the group. |
| `PushAudioFrame(sessionId, channel, pcm16k)` | One frame: 16 kHz mono little-endian int16, base64 over the JSON protocol, at most 32 768 bytes and an even byte count — a bound the hub method asserts itself; see the agent-knowledge entry on why. `channel` is `Mono`, `You` or `Others`, spelled exactly as the REST DTO. |
| `EndSession(sessionId)` | Ends the session as `Completed`. |

Server → client:

| Event | Payload |
|---|---|
| `transcriptionSegmentCommitted` | `{ sessionId, seq, startMs, endMs, text, channel, confidence }` |
| `transcriptionPartialUpdated` | `{ sessionId, channel, text }` |
| `transcriptionSessionStatusChanged` | `{ sessionId, status }`, one of `Completed`, `Cancelled`, `Abandoned`, `Overloaded`, `Failed` |

Every hub-side rejection is a typed `HubException` from `TranscriptionHubErrors`. The replay bound is
`Transcription:SegmentReplayLimit` (500, roughly fifteen minutes of two-lane speech); a truncated replay sets
`replayTruncated`, and `lastSeq` is always the last row actually delivered, so a client resubscribes from where it
left off rather than from where the server wishes it had.

### The segmenter

`LiveTranscriptionSegmenter` (`Client.Application/Services/Transcription/Live/`) turns one channel's raw PCM into
committed segments. One instance is one lane; it is not thread-safe, and its owner (the registry, below) serializes
calls into it per lane.

- **The clock is audio time, not wall time**, derived from the cumulative pushed byte count via
  `WavPcm16.BytesPerMillisecond` (32, at 16 kHz mono int16) rather than summed per frame — a per-frame sum would let
  a frame under one millisecond of bytes never advance the clock at all.
- **A tick fires every second of audio** and asks whether a window is due.
- **The watermark is the whole de-duplication mechanism, never text matching.** A returned segment commits when its
  end is at least `TailGuardMs` (800 ms) before the current audio end, its text is non-empty, and its start is at or
  after the watermark; a segment starting before the watermark is discarded because overlapping windows re-transcribe
  audio already said.
- **At `MaxWindowSeconds` (clamped 2–10, default 5) the window is force-committed** with the tail guard suspended,
  so no window ever exceeds the cap; a push that would cross it is split rather than accepted whole.
- **A graceful end flushes the retained tail** with the same guard suspended, so audio shorter than one window still
  reaches the model at least once.
- **The known ceiling: one word may be inserted, dropped or duplicated per forced boundary.** Windows are cut with
  no overlap, so a word straddling a forced cut is the model's guess from a fragment. `LiveSegmenterGoldenTests`
  bounds this — the committed transcript's word-level edit distance against a whole-clip transcript may not exceed
  the number of forced boundaries in the fixture. Recorded evidence: `ggml-base` inserts "to" into "ask not what" at
  its 5 s cut on `jfk.wav`; `large-v3-turbo` duplicates "you" across the same cap's 7.1 s boundary. The upgrade path
  is whisper.cpp's stream-style `keep_ms` window overlap with token-level de-duplication, not implemented here.

### The registry

`LiveTranscriptionSessionRegistry` is a singleton with **no hosted service** — every timer comes from
`TimeProvider`. One session holds one lane per channel and one session-wide commit lock:

- `PushAudioAsync` is the in-process audio entry point; `TranscriptionHub` merely forwards `PushAudioFrame` into it,
  which is also how [Windows per-application capture](#windows-per-application-capture) feeds a lane without a
  socket — `AttachProducer` / `ILiveAudioProducer` / `LiveProducerRegistration` are the seam for a non-hub producer.
- Every commit — allocating `Seq` (from 1, or after the row's last persisted seq), persisting when the session is
  `Persist`, then publishing — crosses one session-wide lock. **Persistence is a subscriber of the commit, never its
  source**: a persist-free dictation session (S6) streams the identical event sequence with no rows behind it.
- **One termination path, `EndAsync(reason)`, with six callers:** the hub's `EndSession` (`Completed`);
  cancel/delete via `TranscriptionService.CancelAsync` (`Cancelled`); the disconnect grace,
  `Transcription:AbandonedSessionGraceSeconds` (60 s) with no hub connection left (`Abandoned`); a 60 s
  producer-attachment deadline with **no producer ever attaching** — `AttachProducer` disarms it, not the first
  frame, so a silent native capture is not reaped (`NeverAttached`); the pending-audio budget —
  two windows' worth, capped at 640 KB — exceeded (`Overloaded`); and a stalled lane (`Failed`). Only `Completed`
  flushes the retained tail; every other reason aborts in-flight inference instead, so stopping stays prompt.
- Persisted status mapping: `Completed` → `Completed`; `Cancelled`, `Abandoned`, `NeverAttached` → `Cancelled`;
  `Overloaded`, `Failed` → `Failed` with error codes `live-overloaded` / `live-failed`. A graceful end whose final
  flush throws, or whose lane did not drain inside its bound, finalizes as `Failed` with `live-flush-failed` rather
  than reporting `Completed` over a transcript that is missing its last seconds; the committed rows stay readable.
- A commit that arrives after the session is finalized (a lane that outlived its drain bound) is dropped with a
  warning: nothing is persisted or published after the terminal status push.

### Known gaps

- A persist-free session's reconnect replays nothing — there are no rows to replay from.
- A reconnect mid-session sees partial text only from the next tick onward, not the in-flight one.

### Golden fixture

`LiveSegmenterGoldenTests` replays `XE-Local-AI-Engine.Tests/Fixtures/Transcription/jfk-golden-base.json`, recorded
against the real runtime on the CPU backend:

| Component | Value | SHA-256 |
|---|---|---|
| Model | `ggml-base.bin` | `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` |
| VAD | `ggml-silero-v6.2.0.bin` (pinned) | `2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987` |
| Clip | `jfk.wav` | `59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e` |

Re-record with the opt-in `WhisperGoldenFixtureRecorder`, which needs a running server named by
`XE_WHISPER_GOLDEN_SERVER_URL`.

## Browser capture and the live UI

The browser is the only capture device this feature has today. Everything below lives in
`src/features/transcription/capture/`, `hooks/` and `components/` — see
[React Client](10-react-client.md) for where each file sits.

### The chain

`getUserMedia` (microphone) or `getDisplayMedia` (system audio) yields a `MediaStream`. `startPcmCapture` builds one
`AudioContext` per stream, asking for 16 kHz and falling back to a bare constructor on the `NotSupportedError` MDN
documents for an unsupported rate, then loads `Pcm16DownsamplerWorklet.js` onto it and wires
`source → worklet → zero-gain → destination`. The gain node is load-bearing twice: a Web Audio node is only pulled
when it reaches the destination, and a gain of 0 is what stops the microphone being played back through the
speakers. A context created before the page's first user gesture starts *suspended* under the autoplay policy and
pulls nothing at all, so `startPcmCapture` resumes it — a no-op on a context that is already running.

The worklet reads its own `sampleRate` global, which is the context's **real** rate whether or not the 16 kHz request
was honoured, and resamples to 16 000 unconditionally (a ratio of 1 is a copy). It fills a `250 ms` int16 frame —
4 000 samples, 8 000 bytes, comfortably inside the hub's 32 KiB cap — and `postMessage`s the buffer as a transfer, so
nothing is copied across the thread boundary. `useLiveCapture` hands each frame to `useTranscriptionHub.pushFrame`,
which base64-encodes the little-endian bytes and invokes
`PushAudioFrame(sessionId, channelOrdinal, base64)`. The JSON protocol is the reason for both conversions:
System.Text.Json reads a JSON **string** into the hub's `byte[]` as base64, while a `Uint8Array` would stringify as
`{"0":…,"1":…}`. The channel travels as the `TranscriptChannel` **ordinal** (`Mono` 0, `You` 1, `Others` 2), not as
the PascalCase spelling the server→client events use.

The worklet is one self-contained `.js` file with **no imports of any kind**, imported by `PcmCapture.ts` for its URL
alone through Vite's `?url`. Vite's asset plugin emits the file's raw bytes and does not traverse its module graph, so
a sibling module would ship as an unresolved specifier and `registerProcessor` would never run — with every build gate
still green. Its class and its `registerProcessor` call both sit inside a `typeof AudioWorkletProcessor !== "undefined"`
guard, which is what lets a vitest test import the file and exercise the real `downsampleToInt16` the browser runs.

### Starting a session: two steps, and the ordering they buy

Creating the session and starting capture are **separate user actions**. The new-session dialog creates the row and
navigates to `/transcription/{sessionId}`; the operator then presses **Start capture** there. That split exists so
`getDisplayMedia` runs inside the activation window of a real click rather than after a create request and a
navigation.

Within `useLiveCapture.start` the order depends on whether a display picker is involved:

| Path | Order |
|---|---|
| Microphone only | `await live/start` → `getUserMedia` + worklet → forward PCM |
| System audio | `getDisplayMedia` **synchronously, before the first `await`** → `await live/start` → worklet → forward PCM |
| Both | `getDisplayMedia` **synchronously, before the first `await`** → `await live/start` → `getUserMedia` + worklet → await the display promise → forward PCM |

`getDisplayMedia` requires transient user activation *at the moment it is invoked*, and the Screen Capture
specification requires rejection when that activation is absent — so awaiting anything first (the endpoint, a
permission prompt, `addModule`) can spend the gesture and the picker never appears. Invoking the picker is not
producing audio: the stream is acquired but **no frame is forwarded until `live/start` has returned 200**, on every
path. The constraint reaches the call site too, and `useLiveCapture`'s doc comment says so: `start` must be called
straight out of the click handler, with nothing awaited in between. A future refactor that routes the click through a
confirmation dialog or an async guard breaks system audio silently, and the call-order tests in
`useLiveCapture.test.ts` are the only thing that catches it.

Any failure on that path disposes everything: the display promise is settled first so its stream is registered, then
every acquired source is stopped and `EndSession` is invoked if the session had already been opened. A cancelled
picker never leaves a hot microphone, and a refused start never leaves a screen share running.

Leaving the page during a start disposes everything too, and by a different route. The unmount teardown stops what the
hook's source ref holds *at that instant*, which on a `both` session with the picker still open is not what the start
ends up acquiring. `start` therefore carries a generation token, bumped by every teardown: after each of its awaits a
moved generation stops everything acquired so far and returns without touching state. Without it the microphone
acquired after the teardown belonged to no one and stayed hot for the life of the tab.

**Start capture** is disabled until the hub subscription is up, with the reason rendered beside it. A capture begun
before then acquires the devices and fails on its first frame a quarter of a second later, which is a worse way to
learn the node is not there.

### Channels

D2, and no mixing anywhere. The lanes are the node's (`TranscriptionService.LiveChannelsFor`), and the browser sends
on exactly those: microphone alone is `Mono`; system audio alone is `Others` (the lane a Windows per-process capture
feeds too); **both** makes the microphone `You` and the system `Others`. A frame on a lane the session did not
register is refused as `transcription-unknown-channel`. Two sources are two lanes transcribed separately; there is no
diarization behind the labels. A stereo source is downmixed to mono by the audio graph before the worklet
(`channelCount: 1, channelCountMode: "explicit"`), never by dropping a channel.

### Backpressure: refused, never dropped

`pushFrame` allows at most **two** frames in flight and rejects with `CaptureError("overloaded", …)` beyond that;
a transport that is not `Connected` rejects with `CaptureError("disconnected", …)`. Both stop capture identically, but
they are different diagnoses and the string the operator reads is the whole of what they act on — "this node could not
keep up" sends them after a performance problem the node does not have. A rejected frame is speech the node did not
receive, and a hole nobody is told about is worse than a stopped capture — so `useLiveCapture` stops every source, ends the session and shows the
named error. The node's own `Overloaded` status push (the pending-audio budget exceeded) takes the same route. Frames
are never silently discarded.

### Reconnect and the replay drain

`useTranscriptionHub.connected` is true only once the transport is connected, the `SubscribeSession` snapshot has
resolved **and** every truncated replay page has drained. Pushes that arrive while the snapshot is in flight are
buffered and then merged through the same code path as live ones, sorted by sequence. The merge is identity on `Seq`:
an existing sequence is a no-op, so the first text for a sequence wins and an out-of-order lower sequence is still
rendered. A status that arrived live is never overwritten by a snapshot resolving afterwards.

A drain walks its own cursor page by page; the **published** watermark only moves after the last page, or when a live
push carries a higher sequence. If a page fails to advance the cursor, the hook stops draining, leaves the published
watermark where it was and surfaces `transcription-replay-stalled` — rendered as a warning telling the operator the
transcript on screen may be incomplete and a reload will fetch it again. Pushes arriving behind that frozen cursor are
dropped rather than buffered: nothing would ever release them, and the only thing that can complete this transcript is
the reload the warning asks for. A reconnect resubscribes from the watermark, never from zero. Unsubscribing on
cleanup is what arms the node's abandonment grace.

A refused subscription is named, not swallowed. `withAutomaticReconnect` does not retry an initial start, so a node
with transcription switched off, or a failed first negotiate, would otherwise leave the view empty and permanently
disconnected with no signal at all. The hook surfaces `subscribeFailed` carrying the hub's own refusal code
(`transcription-disabled`, `transcription-session-not-found`, … or `transcription-subscribe-failed` for anything it
did not name), and `CaptureControls` renders it as an alert. Nothing retries it; the operator reloads.

### What the session page renders

A session whose source kind is not `File` and whose status is `Created` or `Transcribing` renders the hub-fed
`LiveTranscriptPanel` plus `CaptureControls`; every other session — every `File` session, and every finished one —
renders the persisted rows from the detail endpoint. The panel shows the committed list plus one provisional line per
channel, dimmed and italic and never in the committed list, behind a persisted "show provisional text" toggle. The
microphone a live session captures from is the one it was created with: `TranscriptionCaptureStore` keeps
`deviceIdBySession`, written by the create path against the id the node just returned and read back here. The device
is never sent to the node — capture is client-side — so that store is the only record, and a single global slot let
the session created second decide what the session created first captured from. The
elapsed counter reads the last committed `endMs`, which is audio time, not wall time: the transcript's own clock is
the honest one. When a terminal status arrives on the hub the page invalidates the session queries, so the REST rows
take over from the panel with the node's final flush included.

### Sharing is re-prompted, every time

For the two system-audio sources the dialog carries a permanent hint: the browser asks which screen or window to
share **every time a session starts**, the operator should pick a whole screen and tick "share audio", and this app
never records the picture (the video tracks are stopped the moment the stream arrives). That is accepted browser
behaviour, not something engineered around — there is no persistent screen-capture grant to reuse.

### There is no resume after a reload

Reloading the page tears capture down: the unmount stops every source and closes the graph. The session then hits the
abandonment grace described under [The registry](#the-registry) and is cancelled, keeping everything already
committed. This is the intended behaviour, and it is the reason the replay-stalled warning says "reload" only as a way
to re-read a transcript, never as a way to continue recording.

### The system-audio coverage boundary

The Playwright suite drives Chromium's fake audio device, which replaces the **microphone only**; headless Chromium has
no equivalent fake for `getDisplayMedia`. The system-audio path is therefore covered by frontend unit tests and by a
manual live check, never by the E2E — a real boundary, not an oversight. See
[Testing & Validation](13-testing-and-validation.md#xe-local-ai-enginetestse2etests--playwright).

## Windows per-application capture

The one capture source that is **not** the browser. WASAPI process loopback records the audio of a single running
application on the node itself, converts it in-process and pushes it into the session's `Others` lane through
`ILiveTranscriptionSessionRegistry.PushAudioAsync` — the same in-process seam `TranscriptionHub.PushAudioFrame`
forwards into. **No PCM crosses SignalR on this path**: the browser picks a process and starts the session, and the
node does the rest. Everything below lives in
`XE-Local-AI-Engine.Client.Application/Services/Transcription/Capture/`.

### Packaging: one NuGet on `Client.Application`, no new project, no Windows TFM

`NAudio.Wasapi` 3.1.0 is a `PackageReference` on `XE-Local-AI-Engine.Client.Application`, pinned centrally in
`Directory.Packages.props`. No project was created and no `TargetFramework` changed. Both rejected alternatives cost
more than they buy:

- **A `Providers.WindowsAudioCapture` project** may reference only `Providers.Abstractions`
  (`LayerDependencyTests.ApprovedProjectReferences`), so it could not see `ILiveTranscriptionSessionRegistry` — the
  one thing the capture pump exists to reach. Making it legal would mean pushing `IProcessAudioCaptureSource` down
  into `Providers.Abstractions`, adding an inbound edge, a solution entry, two architecture-test registrations and a
  project-layout row, all so roughly two hundred lines of glue could live one assembly further away. Process loopback
  is neither a model runtime nor a model source, which is what `Providers.*` is for.
- **Multi-targeting `net10.0;net10.0-windows10.0.19041.0`** is unnecessary. The **package id is the whole trick**:
  the `NAudio` meta-package multi-targets and drags in Midi/Asio/WinMM, which would force a Windows TFM onto this
  project, while the leaf `NAudio.Wasapi` ships a single plain `lib/net9.0` asset carrying an assembly-level
  `[SupportedOSPlatform("windows")]` precisely so cross-platform consumers build on Linux without
  `EnableWindowsTargeting`. `Directory.Build.props` sets one `TargetFramework` for the whole repo; overriding it in
  one csproj would be a repo-wide first with no payoff.

That assembly-level attribute plus `AnalysisMode=All` and `TreatWarningsAsErrors` makes every use of an NAudio WASAPI
type from an unattributed call site a **CA1416 build error**. The repo's existing answer applies: a type-level
`[SupportedOSPlatform("windows")]` on `WindowsProcessAudioCaptureSource` (as `WindowsImageJobObjectProcessHandle`
does) and an `OperatingSystem.IsWindows()` branch at the single DI call site in `AddNodeTranscriptionExtensions`,
which is what makes the attribute honest. **There is no CA1416 suppression anywhere in the feature**, in product code
or in tests — a suppression here is exactly how a Windows-only call reaches a Linux host.

### Two version numbers, two jobs

They are deliberately not merged, and neither is redundant:

| Number | Where | What it decides |
|---|---|---|
| **20348** | `ProcessAudioCaptureSupport.MinimumWindowsBuild`, read by `IsSupported` | The **capability policy**. Microsoft documents `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` — the struct that actually carries the loopback mode — as Windows Server 2022 / build 20348. |
| **19041** | `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)` inside `CaptureAsync` | The **analyzer guard**. NAudio annotates `WasapiRecorderBuilder.WithProcessLoopback` `[SupportedOSPlatform("windows10.0.19041.0")]`, and a class-level `"windows"` does not satisfy a versioned API. |

20348 is the higher floor, so the analyzer guard is unreachable in practice — the analyzer cannot know that. The
conservative number was chosen because being wrong this way hides a feature, while being wrong the other way is a hard
COM failure the operator cannot act on. **The conflict is unresolved and is resolved by evidence, not by editing the
constant**: if capture is observed working on a build between 19041 and 20347, report the observation rather than
silently lowering `MinimumWindowsBuild`.

`IsBuildSupported(int major, int build)` takes no `minor` parameter. Every Windows 10 and 11 release reports major 10,
minor 0, and an unused parameter is an `IDE0060` build error here. It takes the version rather than reading
`Environment.OSVersion` so the policy is testable on every operating system rather than only on the one it describes —
comparing `IsSupported` against its own predicate would be a tautology that cannot fail.

### There is exactly one scope, and it is not a choice

`ProcessLoopbackMode` has two members and **neither means "the target alone"**:

| Member | What it captures |
|---|---|
| `IncludeTargetProcessTree` | the target process **and its descendants** |
| `ExcludeTargetProcessTree` | **everything on the endpoint except** the target and its descendants |

Read quickly, `ExcludeTargetProcessTree` looks like "just this application". It is the opposite: offering it as a
"this application only" scope would record every other application on the box while the interface claimed one — a
privacy inversion. `WindowsProcessAudioCaptureSource.CaptureMode` is `IncludeTargetProcessTree` and is the only mode
this product has: there is no scope enum, no scope field on `StartProcessCaptureRequest`, and
`ExcludeTargetProcessTree` appears nowhere in product code.
`WindowsProcessLoopbackTests.CaptureUses_IncludeTargetProcessTree_Never_Exclude` asserts the constant so it cannot
creep back.

The consequence binds the user interface: the copy promises **the application *and its child processes***, never
"only this application". Excluding a target's children is not implementable on Windows through this flag — it would
need a different mechanism entirely — and is recorded as a limitation rather than approximated.

### Format: no `WithFormat`, convert in managed code

The recorder is built with `BuildAsync()`, never the synchronous `Build()`, which throws once process loopback is
configured — the activation path is asynchronous. No format is requested. The process-loopback virtual device rejects
`AutoConvertPcm`, so a format it does not accept is an `IAudioClient::Initialize` failure rather than a silent
resample; taking NAudio's documented 44.1 kHz stereo float fallback is the one path that always initialises, at the
cost of a resample the target hardware will not notice. **What `recorder.WaveFormat` actually reports on a real
Windows box is not verified here** and belongs in the operator's round.

`Wasapi16kMonoPcmConverter` turns whatever arrives into the 16 kHz mono little-endian int16 the segmenter accepts:
`BufferedWaveProvider` → `ToSampleProvider()` → `StereoToMonoSampleProvider` (only when stereo) →
`WdlResamplingSampleProvider` (only when the rate differs), so a source that is already 16 kHz mono degenerates to a
format conversion and no call site needs a branch. It is written against `NAudio.Core` only — no WASAPI type appears
in it — which is why it is fully managed, runs on Linux and is **the one part of this feature the CI gate actually
executes**. `WdlResamplingSampleProvider` is what buys that; `MediaFoundationResampler` would not.

Three details are load-bearing:

- **`ReadFully = false`.** The default is `true`, which zero-fills an underflow and returns the full requested count —
  a drain written as `while (Read(buffer) > 0)` would then never terminate and would manufacture unlimited silent PCM
  that the segmenter would faithfully transcribe as an endless empty window. `Converter_ReturnsZeroOnAnEmptyFollowUpRead`
  and `Converter_TotalOutputIsBoundedByTotalInput` both fail if the default is left in place.
- **The drain is bounded** at 64 iterations per WASAPI packet, in 32 000-byte chunks (one second of output). The bound
  is belt-and-braces: `ReadFully = false` already terminates the drain, and one packet can never produce more 16 kHz
  mono output than it carried input, so tripping the cap means a bug in the conversion chain.
- **The float→int16 scale is 32768, clamped asymmetrically** to `[short.MinValue, short.MaxValue]`. NAudio's
  int16→float conversion divides by 32768, so scaling by 32767 would cost one least-significant bit on exactly the
  passthrough case this converter must not degrade; with 32768 the already-16 kHz-mono test asserts **exact**
  equality.

`DiscardOnBufferOverflow` is `false`: the pump drains after every packet, so a full buffer means the conversion
stopped keeping up and the session should fail visibly rather than quietly losing the newest audio.

### The coordinator, and why it is not the producer

`ProcessAudioCaptureCoordinator` is a singleton registered on **every** operating system, `IAsyncDisposable`, running
captures on tasks that outlive the request that started them — a cancelled HTTP request must not kill a recording the
operator is still making.

- **The coordinator is not itself `ILiveAudioProducer`.** `ILiveAudioProducer.StopAsync` carries no session id, so one
  singleton attached to several sessions could not tell which session the registry meant. A private per-session
  `SessionCapture` is the producer instead: it owns that session's linked cancellation source, its capture task and
  its detach handle, and it is what `AttachProducer` receives.
- **Attachment happens before the recorder exists**, and **attaching is what satisfies the producer-attachment
  deadline** — waiting for the first frame does not. `AttachProducer` disposes the session's `AttachmentTimer` inside
  the session gate, right after the admission check. A native capture of an application that happens to be silent
  pushes nothing, because WASAPI never yields a silent packet at all, so gating on audio would have reaped a session
  with a healthy running recorder as `NeverAttached` once the deadline elapsed. The browser abandonment grace is
  untouched: a closed tab still ends the session whatever else is feeding it.
- **Attach and stop are one critical section, under `SessionCapture`'s own `Lock`.** The coordinator publishes the
  handle into its dictionary **before** calling `Attach`, so a stop can land in the gap and find nothing to cancel.
  It records the request instead; `Attach` publishes the registration and then, **before scheduling anything**,
  honours a recorded stop and returns, all inside the lock. That ordering is load-bearing: scheduling the loop first
  and cancelling afterwards left a window, because the worker does not take this lock, so it could clear its
  cancellation check and enter `CaptureAsync` — building a WASAPI recorder *after* the stop had already returned —
  before cleanup got to cancel. Not scheduling at all is the only ordering with no window, and it leaves `Capture` as
  a completed task, which every caller already tolerates. Teardown itself is one method, `CleanUpLocked`, which cancels,
  detaches and disposes **exactly once and in that order** — cancel always before dispose, one caller owning both,
  so no stack ever sees a half-torn-down handle. A stop that arrives before anything is published simply drops the
  dictionary entry and leaves the teardown to `Attach`, which sees the recorded request under the same lock.
  Without this the capture could run **untracked** — absent from the dictionary, so no later stop, no `IsCapturing`
  and no `DisposeAsync` could reach it — while it kept pushing PCM into a session whose operator had been told
  capture stopped; the other variant disposed the source out from under `Attach`, whose next `.Token` read threw
  `ObjectDisposedException`, which derives from `InvalidOperationException` and so was caught by `Start` and
  reported as `SessionNotLive` for a capture that had in fact started. The cancellation token is likewise read into
  a local **before** the task lambda, because a lambda that reads `.Token` when the pool thread runs it can find the
  source already disposed. `RunAsync` throws on an already-cancelled token before it builds a recorder.
- **Cancelling the registry's `ProducerToken` is the one stop signal.** Every handle links its own source to that
  token, so ending a session for any reason — including the pending-audio budget overflowing into `Overloaded` —
  stops the capture at its next iteration. A private token the registry cannot reach is how a recorder outlives its
  session.
- **`Start` is synchronous** and returns `StartProcessCaptureOutcome` (`Started`, `NotSupported`, `SessionNotLive`,
  `AlreadyCapturing`). Nothing in it performs I/O, and a `Task`-returning start would suggest it waits for the
  capture, which is exactly what it must not do. It asks `IsLive` before attaching so the caller gets a typed refusal
  rather than a recorder with nowhere to push; the registry re-checks under its own lock, and a session that stopped
  being live in between surfaces as a refused attach, not a stranded producer.
- **`StopAsync` cancels and returns — it never awaits the capture task.** A `PushAudioAsync` blocked behind inference
  would otherwise hold the registry's bounded producer-stop wait. `StopAsync_ReturnsWhileAPushIsBlockedBehindInference`
  is the proof: adding an `await` on the capture task turns it red.
- **The coordinator never ends a session and names no session status.** Stopping capture and ending a live session are
  different acts; the second belongs to the registry's single `EndAsync` path.
- **`DisposeAsync`** stops and detaches every capture, then bounds the drain at three seconds on the injected
  `TimeProvider` — a bound on shutdown, not a wait for an event, so one wedged capture cannot hold the process open.

### The call order

Unchanged from the live path, with one step added at the end:

1. `POST transcription/sessions` with `SourceKind = ApplicationProcess`.
2. `SubscribeSession(sessionId, 0)` on `TranscriptionHub`.
3. `POST transcription/sessions/{sessionId}/live/start` — awaited.
4. `POST transcription/sessions/{sessionId}/capture/process` with `{ processId }`.

`StartProcessCaptureEndpoint` verifies step 3 happened by asking the registry, because starting a recorder for a
session with no lanes would capture audio with nowhere to put it. The two refusal families are different on purpose:
**400** `capture-not-supported` when the host cannot capture process audio at all, because retrying can never work;
**409** `session-not-live` or `capture-already-running`, because the caller can fix either and try again. Both carry a
reason code rather than prose, in the shape `TranscriptionRuntimeBlockedResponse` already uses. `ProcessId` must be
positive — `StartProcessCaptureRequestValidator` rejects anything else with a 400 — and the generated client types
`processId` as **optional** (`processId?: number`), so the server-side check is the only one there is.

**Ending the session is the hub's `EndSession`**, which runs the registry's single termination path and, by cancelling
`ProducerToken`, stops the node's recorder with it. The `DELETE` route stops **only** the capture and leaves the
session live; it is a convenience over the termination path, never an alternative to it, and the SPA does not call it
— `useLiveCapture`'s teardown ends the whole session instead, over the hub where it can and over the REST cancel
route where it cannot (see [the REST fallback](#stopping-when-the-hub-cannot-deliver-the-rest-fallback)).

In the browser this source is the odd one out: `useLiveCapture` acquires **no** source, opens no `AudioContext` and
forwards no frame for `{ kind: "process" }`. It posts the capture request after `live/start` returns and moves
straight to the capturing phase, and `TranscriptionSessionPage` treats an `ApplicationProcess` session as live through
an explicit allow-list rather than "everything but `File`", because the browser contributes nothing to it. The
*Application audio* source appears in the new-session dialog only when the runtime status reports
`processCaptureSupported` — hidden on an unsupported node, never disabled.

The runtime-status response carries `processCaptureSupported`, populated from `IProcessAudioCaptureSource.IsSupported`
by `TranscriptionRuntimeService` and projected through `TranscriptionRuntimeView`. It lives on the view rather than
being passed in at the endpoint because two endpoints project that view and two places could disagree; the positional
parameter is required, so a forgotten wire-up cannot silently report `false`.

### Silence never arrives, and the audio clock lags because of it

NAudio's `WasapiRecorder.CaptureAsync` yields only packets whose `AudioClientBufferFlags.Silent` bit is clear —
**silent buffers never reach this product at all**. Nothing here special-cases them; the drop happens upstream. The
consequence is real and worth knowing before reading a transcript: the segmenter's clock is audio time, derived from
the cumulative pushed byte count, so during silence the `Others` lane's clock **lags wall time**. The VAD-driven
segmenter tolerates this, and the timestamps stay internally consistent, but they are not wall-clock offsets from the
start of the recording. Writing silence instead would require the zero-copy `DataAvailable` event, whose buffer is a
`ReadOnlySpan<byte>` valid only inside the callback and therefore cannot cross an `await`.

### A capture that dies leaves the session live and silent

The companion consequence, and the sharper one. A detached capture can end on its own in several ways: `BuildAsync`
refuses because the pid is already gone or CoreAudio refuses the activation, the target process exits mid-capture, or
the converter's buffer overflows because conversion stopped keeping up. All of them are caught and logged by the
capture loop, which then removes its own handle. **Nothing ends the session.** By that point `Start` has already
answered 200 with `capturing: true`; the producer attached, which disarmed the registry's producer-attachment
deadline, so the `NeverAttached` sweep will not fire either; and S5 never calls `EndAsync`,
because stopping a capture and ending a live session are different acts. The session therefore stays live, receives
nothing more, and the only client-visible signals are indirect: `IsCapturing` reports `false`, and a later `DELETE`
on the capture route answers **404** instead of 204. The browser abandonment grace is the one thing that still ends
such a session, and only if the operator closes the tab.

This is intended rather than overlooked — the coordinator's own remarks state that it never ends a session and names
no session status, so that the registry keeps exactly one termination path. It is also the least pleasant thing about
the design from the operator's seat, which is why the Windows round has to record what actually happens: killing the
target process mid-session is a step in the live-validation plan, and what the transcript, the status and the capture
route report afterwards is the observation that decides whether a future slice needs a "capture ended" signal on the
hub.

### Stopping when the hub cannot deliver: the REST fallback

Every client stop funnels through one teardown, and that teardown now has two ways to reach the node.
`useTranscriptionHub.endSession` **reports delivery**: it resolves `false` when there is no connected transport to
invoke `EndSession` on. A throwing invoke is treated the same way, but for a different reason — the client cannot
tell whether the node processed the call before the transport dropped, so it assumes the worst. In both cases
`useLiveCapture` falls back to the REST cancel route, `POST transcription/sessions/{sessionId}/cancel`, which reaches
the registry's single `EndAsync` path with `LiveEndReason.Cancelled` and stops the producer at once. Sending it after
an `EndSession` that did land is **redundant and harmless**: `BeginEnd` is idempotent under the session gate — a
session that already has an end task returns it untouched — so the first end wins and keeps **its own** reason. A
session genuinely completed over the hub therefore stays `Completed`; the late cancel changes nothing. The fallback
covers **every** stop path, including `abandon(true)` — the unmount that lands while `live/start` is still in
flight — and it applies to every request kind, not only process capture.

**A stop that neither transport acknowledged is not dropped.** It is held pending, surfaced to the operator as
`stop-failed` rather than a silent return to idle, and redelivered the moment the hub reconnects. That redelivery
matters because the reconnect is the *same* event that re-subscribes and disarms the node's abandonment grace: without
it, a stop whose only record lived in this browser would never reach the node at all, and the grace that would
otherwise have caught the session has just been disarmed. The pending flag clears on the first acknowledgement from
either transport, and clearing it only resets the `stop-failed` message — a teardown triggered by an `overloaded`
push keeps saying so.

**A stop that actually ends the session over REST finalizes the row as `Cancelled`, not `Completed`.** That matters: only `Completed`
flushes the segmenter's retained tail, so a session ended over REST keeps every committed segment but not the last
partial window. It is the same status an abandoned session already receives, so the outcome does not change with the
delivery route — what changes is the timing.

Before this existed the failure was silent and open-ended. A Stop pressed while the transport was down ended nothing,
the view went idle, and the reconnect re-subscribed and disarmed the node's abandonment grace, leaving the session —
and for per-application capture, the recorder on the operator's application — running **indefinitely**. For a
browser-fed source the same gap left a session stuck in `Transcribing`, the same bug with a quieter symptom.

The 60 s browser abandonment grace (`Transcription:AbandonedSessionGraceSeconds`, armed on the last hub disconnect,
ending the session as `Abandoned`) survives as the backstop for the one case no client code can cover: a browser that
**dies without running teardown at all** — a crash, a killed process, power loss. In that window the node keeps
capturing the target application, because the recorder lives on the node and has no idea the browser is gone. The
clock starts at the **disconnect**, not at the unmount, so the exposure is at most a minute and often less. The
Windows round should still record what lands in the transcript after a tab is closed mid-capture, since that is the
path the fallback is supposed to make instant.

### Known limitations

- **The target process exiting mid-capture is caught and logged**, not raised: the capture loop treats any failure as
  "capture ended", the session lives on until something ends it through the registry, and nothing escapes as an
  unobserved task fault. Which of the two shapes NAudio produces there — a clean end of the sequence or a COM
  exception — is not verified.
- **More than two channels is refused** with a `NotSupportedException`. `StereoToMonoSampleProvider` downmixes two
  channels only, and failing loudly beats interleaving channels into the transcript. Unreachable with NAudio's stereo
  default.
- **One application playing to two render endpoints enumerates twice**, and so does one holding an idle session
  beside a playing one. `ProcessAudioCaptureCandidates.Aggregate` de-duplicates by process id and **ORs** activity
  across that process's sessions, because activity is a property of the process, not of whichever session came back
  first — keeping the first row seen made the answer depend on enumeration order and reported a playing application
  as silent. The aggregation is pure and free of any WASAPI type, so it runs and is tested on Linux; only the
  enumeration is Windows-only. Which endpoint capture then attaches to is WASAPI's choice, not this product's.
- **A box with no active render endpoint returns an empty picker**, which is correct but indistinguishable from "no
  application is holding a render stream" — hence the `supported` flag, which separates "this operating system cannot
  do it" from "there is nothing to offer right now".
- **System-sounds sessions are skipped, and so are expired ones.** `hasAudio` reports whether the session is
  `Active` **right now**, so an application that has gone quiet stays listed with `hasAudio: false` and remains a
  valid capture target; it leaves the picker only when its session expires, which is the process releasing its render
  stream or exiting. The flag is a "playing right now" hint for the operator choosing a row, never a statement about
  whether the application can produce sound at all.
- **A process that exited between enumeration and name lookup lists as `PID n`** rather than disappearing.
- **The chosen process id is remembered only in the browser**, in `TranscriptionCaptureStore` beside the microphone
  device id, because the node is told which process to capture at start rather than at create. A session whose stored
  id is gone cannot start capture and says so; creating a new session and picking again is the way out.

### Every other operating system fails closed

The DI branch is `OperatingSystem.IsWindows()` and nothing finer: Linux and macOS get
`NotSupportedProcessAudioCaptureSource`, while a **Windows host below the build floor still gets the Windows source**
and is refused by its own `IsSupported` check. The two paths are deliberately indistinguishable from outside:
`IsSupported` is `false`, the runtime status reports `processCaptureSupported: false`, the picker answers 200 with an
empty list, and `CaptureAsync` **throws a named `TranscriptionProcessCaptureNotSupportedException`** rather than
returning quietly. Failing closed is the point — returning silently would open a live session that never receives a
byte and looks to the operator like a broken microphone. There is no PipeWire or PulseAudio per-process binding behind
the non-Windows case, and none is planned.

### What is verified, and what is not

Everything platform-independent runs on the Linux gate: the documented build floor
(`ProcessAudioCaptureTests.Support_*`), the PCM downmix/resample and its drain bound (`…Converter_*`, against real
`NAudio.Core` types — no mock), the picker's de-duplication and activity OR-ing
(`ProcessAudioCaptureCandidates.Aggregate`, pure and WASAPI-free precisely so a Linux runner can exercise it), the
coordinator lifecycle (`ProcessAudioCaptureCoordinatorTests`, including the
overload and blocked-push cases) and the endpoint policy, shapes and reason codes
(`TranscriptionCaptureEndpointTests`).

**The WASAPI process-loopback path itself was not executed.** The development box is WSL2 with no Windows audio stack,
so the three `[RunOn(OS.Windows)]` tests in `WindowsProcessLoopbackTests` report **skipped** — the honest result, and
not evidence that the feature works. What a real Windows box still has to establish is recorded in the slice plan's
live-validation section: that the picker lists only the process actually playing, that committed segments arrive on
`Others`, that a **second application's audio does not appear** (the negative control, and the whole point of the
single scope), that a multi-process application's child audio does appear, the observed OS build, the observed
`WaveFormat`, and that closing the target mid-capture ends the session through the registry with no recorder left
running.

## React feature

`src/features/transcription/` renders the session list at `/transcription` and one session at
`/transcription/{sessionId}`, under the **Preview** navigation group. Server state is TanStack Query over the
generated hey-api client; the upload is the one hand-written multipart call, following `useKnowledgeUpload`'s axios
precedent because the generated client does not express upload progress. The segment list renders the committed
transcript with a channel badge for any non-`Mono` channel, and "Send to chat" hands the transcript to the composer
through `core/ui/stores/PendingComposerTextStore.ts`, which exists to carry text across a navigation. The live half of
the feature — the capture sources, the worklet, the hub hook and the live panel — is described in
[Browser capture and the live UI](#browser-capture-and-the-live-ui). See [React Client](10-react-client.md).

The **managed source build** is the one part of this feature whose UI does *not* live in `features/transcription/`:
`WhisperRuntimeSourceBuildCard` sits in `features/node-settings/components/` beside `SourceBuildCard` (llama.cpp) and
`ImageRuntimeSourceBuildCard` (stable-diffusion.cpp), rendered on the Node Settings page, because that is where the
other two managed-build lanes are. All three are visible to **every** operator: they were Developer-Mode gated until
2026-09-19, when that gate was removed on the owner's ruling — the routes behind them are Operator-authorized and do
nothing until Build is pressed, and hiding the only GPU-transcription path a Linux + NVIDIA box has behind a mode its
operator has no reason to enable made the feature unfindable. It calls the five
`transcription/runtime/source-build*` routes through `queries/useWhisperRuntime.ts`, and shares
`CudaBuildLogView`, `SourceBuildPrerequisiteList` and the helpers in `models/SourceBuildModels.ts` with the other two
cards — each keeps its own i18n subtree, so the prerequisite list takes the key prefix as a prop. It differs from
them in one way that matters: whisper.cpp registers **no SignalR hub**
(`IWhisperCppSourceBuildEventPublisher` has only its no-op floor and the host substitutes nothing), so the card's
live phase and log come from a three-second poll of the status route, and that poll's terminal transition is what
re-reads the runtime status after an adoption. An invalid managed record shows its sanitized reason and keeps
eject/remove usable, which is the only in-app exit from a fail-closed tombstone. The transcription runtime card
points at it when the resolved backend is `cpu` and no managed build exists — the SPA never infers the host's OS or
GPU, only the backend the daemon reported, so the pointer names the Linux + NVIDIA condition rather than asserting it.

Each card's prerequisite probe really runs the toolchain (`cmake --version`, `gcc`, `g++`, `ninja`/`make`, `git`, plus
`readelf` here and `nvcc`/`nvidia-smi` or `glslc`/`vulkaninfo` for an accelerated backend; `nvcc` is the one tool not
looked up on PATH alone — see `CudaToolkitLocator` in
[Local Runtime & Providers](03-local-runtime-and-providers.md)), so the three of them
together spawn roughly twenty short-lived child processes per probe round. Ungated, that would have been the cost of
every operator's every visit to Node Settings, so **the build form sits behind a disclosure and the probe is `enabled`
only while it is open** — `SourceBuildFormDisclosure` plus `useSourceBuildFormDisclosure`, shared by all three cards.
Everything the node already knows stays outside it (the adopted-build summary, a running build's phase and log, the
failure, Cancel/Eject/Remove): those come from the status route and the hubs, and each status endpoint just returns
in-memory state under a lock. The form opens itself — from status data alone, never by probing — when it is the thing
the operator needs: a build is running, a build ended in a failure, or the managed record is invalid. That expansion is
a one-way latch, so the tick where a build stops running cannot collapse the retry form under whoever is reading it.
Closed, the section is `keepMounted={false}`, so it holds no focusable inputs, and the toggle carries
`aria-expanded`/`aria-controls`. On top of that the three prerequisite queries share `sourceBuildPrerequisiteStaleTime`
(five minutes, in `models/SourceBuildModels.ts`), so reopening the form does not re-probe either; the start endpoint
re-probes server-side regardless, so a stale "can build" can never let an unbuildable request through.

## Options and settings

| Key | Where | Default | What it does |
|---|---|---|---|
| `Transcription:Enabled` | `TranscriptionOptions.Enabled`, read once in `Program.cs` | `true` | Gates behaviour, never registration: the 404 middleware, not a missing endpoint. |
| `Transcription:IdleTimeoutMinutes` | `TranscriptionOptions.IdleTimeoutMinutes` | `15` | Seed for the daemon's idle-unload TTL, used until a node setting is stored. |
| `TranscriptionIdleTimeoutMinutes` | `StoredNodeSettings`, clamped 1–240 | unset | The stored node setting; it wins over the appsettings seed. |
| `TranscriptionSelectedModelId` | `StoredNodeSettings` | unset | The operator's selected whisper model. Unset resolves through `ITranscriptionRuntimeService.ResolveEffectiveModelIdAsync`. |
| `Security:MaxUploadFileSizeMb` | `SecurityOptions.MaxUploadFileSizeMb` | `25` | The upload cap the copy enforces as it writes. |

Per-session options live in the session's encrypted `ConfigJson`: language mode (`auto` / `override`) and the
override code, translate-to-English, the live maximum window in seconds (clamped 2–10) and channel attribution. The
last two are persisted and normalized already, and are read by the live slices.

## What is not here yet

- **Per-application capture anywhere but Windows.** WASAPI process loopback has no Linux or macOS equivalent — no
  per-process binding exists in PipeWire or PulseAudio — and no PipeWire/PulseAudio/PortAudio code is planned. See
  [Windows per-application capture](#windows-per-application-capture) for what Windows gets and how every other host
  fails closed.
- **Capturing an application *without* its child processes.** Not implementable through `ProcessLoopbackMode`; the
  product captures the process tree and says so.
- **Dictation into the agent chat** (S6). It appends in place through a toolbar callback and deliberately does not
  route through the pending-composer store.
- **Diarization.** whisper.cpp clusters no speakers; `You`/`Others` come from transcribing two captured channels
  separately ([ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md) D2).

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the whisper.cpp supervisor, binary acquisition and the managed CUDA build lane.
- [Data & Persistence](08-data-and-persistence.md) — the two tables, the encrypted columns and the migration timeline.
- [API & Hubs](09-api-and-hubs.md) — the `transcription/*` route family and OpenAPI → hey-api.
- [React Client](10-react-client.md) — the `transcription` feature folder and the client conventions it follows.
- [Project Layout](02-project-layout.md) — why the WASAPI package sits on `Client.Application` and stays on a plain `net10.0` target.
- [Testing & Validation](13-testing-and-validation.md) — the live-transcription E2E suites and the Windows-gated process-loopback class.
- [Security & Privacy](12-security-and-privacy.md) — loopback-only endpoints, Operator gating, node-local privacy.
- [ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md) — the decisions this feature implements.
