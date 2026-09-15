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
[Live sessions](#live-sessions)), and the **browser capture** that feeds it (see
[Browser capture and the live UI](#browser-capture-and-the-live-ui)). Windows per-application capture and dictation
are **not built yet** — see [What is not here yet](#what-is-not-here-yet).

The decisions behind the feature — browser-first capture, channel attribution instead of diarization, the managed
Linux CUDA source build, the slice order, and the never-persist-audio rule — are recorded in
[ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md).

## Where the code lives

| Concern | Project / path |
|---|---|
| Runtime supervision, transcriber, argument builder, pins, catalogue | `XE-Local-AI-Engine.Providers.WhisperCpp/` (`IWhisperServerSupervisor`, `IWhisperTranscriber`, `WhisperCppReleasePins`, `WhisperModelCatalog`) |
| Runtime/model application services | `XE-Local-AI-Engine.Client.Application/Services/Transcription/` (`ITranscriptionRuntimeService`, `IWhisperModelDownloadCoordinator`, `WhisperModelPathResolver`) |
| Session lifecycle + batch transcription | `…/Services/Transcription/Implementation/TranscriptionService.cs` (`ITranscriptionService`) |
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
persisted as ints and adding a member later would be a schema change. Only `File` and `Mono` are reachable today.

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
  which is also how S5's Windows process capture will feed a lane without a socket — `AttachProducer` /
  `ILiveAudioProducer` / `LiveProducerRegistration` are the seam for a non-hub producer.
- Every commit — allocating `Seq` (from 1, or after the row's last persisted seq), persisting when the session is
  `Persist`, then publishing — crosses one session-wide lock. **Persistence is a subscriber of the commit, never its
  source**: a persist-free dictation session (S6) streams the identical event sequence with no rows behind it.
- **One termination path, `EndAsync(reason)`, with six callers:** the hub's `EndSession` (`Completed`);
  cancel/delete via `TranscriptionService.CancelAsync` (`Cancelled`); the disconnect grace,
  `Transcription:AbandonedSessionGraceSeconds` (60 s) with no hub connection left (`Abandoned`); a 60 s
  producer-attachment deadline with nothing ever feeding the session (`NeverAttached`); the pending-audio budget —
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

## React feature

`src/features/transcription/` renders the session list at `/transcription` and one session at
`/transcription/{sessionId}`, under the **Preview** navigation group. Server state is TanStack Query over the
generated hey-api client; the upload is the one hand-written multipart call, following `useKnowledgeUpload`'s axios
precedent because the generated client does not express upload progress. The segment list renders the committed
transcript with a channel badge for any non-`Mono` channel, and "Send to chat" hands the transcript to the composer
through `core/ui/stores/PendingComposerTextStore.ts`, which exists to carry text across a navigation. The live half of
the feature — the capture sources, the worklet, the hub hook and the live panel — is described in
[Browser capture and the live UI](#browser-capture-and-the-live-ui). See [React Client](10-react-client.md).

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

- **Windows per-application capture** (S5). Browser capture cannot scope to one application; NAudio's WASAPI process
  loopback can, on Windows only.
- **Dictation into the agent chat** (S6). It appends in place through a toolbar callback and deliberately does not
  route through the pending-composer store.
- **Diarization.** whisper.cpp clusters no speakers; `You`/`Others` come from transcribing two captured channels
  separately ([ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md) D2).

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the whisper.cpp supervisor, binary acquisition and the managed CUDA build lane.
- [Data & Persistence](08-data-and-persistence.md) — the two tables, the encrypted columns and the migration timeline.
- [API & Hubs](09-api-and-hubs.md) — the `transcription/*` route family and OpenAPI → hey-api.
- [React Client](10-react-client.md) — the `transcription` feature folder and the client conventions it follows.
- [Security & Privacy](12-security-and-privacy.md) — loopback-only endpoints, Operator gating, node-local privacy.
- [ADR 0012](../adr/0012-audio-transcription-runtime-and-capture.md) — the decisions this feature implements.
