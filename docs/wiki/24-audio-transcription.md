# Audio Transcription (whisper.cpp)

> Reviewed: 2026-09-12 · Code-grounded.

The node transcribes audio **locally** with [whisper.cpp](https://github.com/ggml-org/whisper.cpp), supervised as a
`whisper-server` child process exactly the way `llama-server` and `sd-server` are. A **transcription session** is a
persisted, encrypted row; its transcript is a list of segments beneath it. The audio itself is **never persisted** —
an upload lives in one engine-owned temporary file for the length of one transcription and is deleted with the object
that minted it.

This page covers what exists today: the runtime and its model catalogue (delivered first, summarized here and
documented in full on [Local Runtime & Providers](03-local-runtime-and-providers.md#providerswhispercpp--the-supervised-speech-to-text-runtime)),
the session/segment data model, and the **batch file-upload** transcription path with its endpoints and React feature.
Live capture, the segmenter and the hub are **not built yet** — see [What is not here yet](#what-is-not-here-yet).

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

Live PCM, when the live slices land, lives only in the segmenter's in-memory ring buffer — the same rule, one layer up.

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

## React feature

`src/features/transcription/` renders the session list at `/transcription` and one session at
`/transcription/{sessionId}`, under the **Preview** navigation group. Server state is TanStack Query over the
generated hey-api client; the upload is the one hand-written multipart call, following `useKnowledgeUpload`'s axios
precedent because the generated client does not express upload progress. The segment list renders the committed
transcript with a channel badge for any non-`Mono` channel, and "Send to chat" hands the transcript to the composer
through `core/ui/stores/PendingComposerTextStore.ts`, which exists to carry text across a navigation. See
[React Client](10-react-client.md).

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

- **Live capture and the segmenter** (S3/S4). The rolling-buffer commit pipeline, `TranscriptionHub`, the live-start
  route and the browser capture UI do not exist. A fixed-window loop against the stateless inference route
  hallucinates on a mid-word cut, which is why the window is a *maximum* and the commit layer gets its own slice.
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
