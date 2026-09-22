# Native desktop 1.0 — delivery and evidence

Status: implementation delivered as a merge candidate; final validation and review are recorded below. Merge requires operator approval; real Ubuntu GUI acceptance remains waived.

Decision record: [ADR 0013](../adr/0013-native-desktop-shell.md)

## Approved direction

- Native desktop is required for 1.0. Keep React, the standalone ASP.NET Core engine,
  REST/SignalR, SQLite, browser and headless operation.
- Thin Avalonia NativeWebView shell in a separate process; no engine assembly references
  or second application API through a JavaScript bridge.
- Windows and Ubuntu LTS x64. Acceptance matrix: Ubuntu 24.04 X11 and Wayland;
  Ubuntu 26.04 stock GNOME Wayland, including its XWayland compatibility path.
- First close asks Keep running in tray / Quit / Cancel, with a remember-choice checkbox;
  preference remains editable. Tray menu: Open XE / Settings / Quit. If the tray is
  unavailable, do not hide the only window.
- Quit stops only a shell-owned engine, with a general interruption warning; a separately
  started engine remains running. No aggregate activity API is needed for that warning.

The operator has authorized completion through final validation and review. Merge into `develop` still requires explicit approval. Request operator interaction when a native Windows check cannot be automated.

The Phase 0 sections below preserve historical probe scope and results. They do not
describe the current implementation; current scope and validation are recorded under
[Production completion checkpoint](#production-completion-checkpoint--2026-09-22).

## Phase 0 implementation (historical)

`XE-Local-AI-Engine.Desktop` is an **attach-only probe**, not a launcher replacement.
`DesktopLaunchOptions.Parse` requires an explicit HTTP loopback origin and absolute browser
profile path. `DesktopWindow` embeds only that origin and sends external HTTP(S) navigation
to the system browser. Other top-level schemes are blocked. No cookies, tokens or page
content are logged. The probe neither starts nor stops the engine, and closing it quits
the probe rather than implementing the future tray preference.

Packages are centrally pinned: Avalonia.Desktop 12.1.2 and Avalonia.Controls.WebView 12.1.0.
The existing architecture tests pin the shell's empty product dependency set.

After building Release, against an independently started **scratch** engine:

```sh
dotnet XE-Local-AI-Engine.Desktop/bin/Release/net10.0/XE-Local-AI-Engine.Desktop.dll \
  --origin http://127.0.0.1:<port> --profile-dir /absolute/scratch/webview-profile
```

Use the actual ready URL, not a guessed port. Keep the engine data directory and
`XDG_DATA_HOME` isolated; never use a real user's data for the probe. Build the production
React bundle before engine startup. A Vite or browser-only result is not native acceptance.
Windows requires WebView2; Ubuntu requires the native WebKit dependencies documented by
[Avalonia](https://docs.avaloniaui.net/docs/app-development/embedding-web-content).

## Go/no-go evidence required

Record each case separately for each supported desktop/session, package and selected
WebView backend. WSLg evidence is supplemental, not an Ubuntu desktop or Windows pass.

| Check | Required evidence |
|---|---|
| Authentication | Sign in on the exact production HTTP loopback origin; refresh/reopen the same profile and retain the session; logout clears it. Do not weaken Secure/HttpOnly cookies or origin guards. |
| Rendering and realtime | Production SPA navigation, SignalR negotiation and WebSocket delivery, reconnect/reconciliation. |
| Media | Secure-context capabilities plus actual microphone permission, audio capture and AudioWorklet execution; API presence alone is insufficient. |
| Files and clipboard | Actual upload, blob-backed export/download, clipboard with user gesture, cancelled dialogs. The probe's blocked top-level blob scheme requires investigation if a backend surfaces downloads as navigation. |
| External navigation | OAuth in the system browser completes against the engine; embedded content cannot navigate to arbitrary sites/custom schemes. |
| Desktop integration | Focus, keyboard traversal, accessibility, tray host availability/disappearance; X11 and Wayland/XWayland evidence distinguished. |
| Update identity | Minimal packaged experiment proves which executable/PID the updater waits for and restarts, including the Windows bootstrap. No production package changes in this phase. |
| Resource pressure | Responsive UI and correct cleanup under a real local model workload, after checking available resources. |

## Later phases identified during Phase 0 (historical)

1. Ownership: reuse readiness/port/lease behavior; inherited parent-lifetime pipe for an owned
   engine, graceful bounded shutdown, attach-safe behavior and second-instance activation.
2. Tray and preferences: first-close prompt, explicit quit, restore/settings, unavailable-tray
   fallback and preference persistence. Keep normal product operations on REST/SignalR.
3. Packaging: preserve Windows prerequisite/Velopack bootstrap; validate Linux main-process
   identity and update/restart arguments; enroll the actual shell payload in license inventories.
4. Acceptance: complete the platform matrix, lifecycle failure injection, backend/frontend gates,
   package/update smoke tests and approved real-model testing; coordinator review before release.

Backend modularization, provider inversion, queue fixes, lifecycle restructuring and SQLite
foreign-key/performance changes remain independent workstreams, not prerequisites of this probe.

## Evidence checkpoint — 2026-09-21

Tested feature baseline: `34318010c` plus this probe, not subsequent changes to `develop`.
The probe is implemented; **Phase 0 acceptance remains open**.

- Full Release backend gate passed: main namespace batches reported 12,324 passes and no
  failures (not a unique-test count); persistence reported 1,204 passes and one opt-in
  performance-profile skip; agent tests reported 392 passes. Assembly guards passed.
  All five `DesktopLaunchOptionsTests` and the architecture guards passed.
- Frontend `validate`, coverage thresholds (463 files / 4,697 tests), 67 tooling tests,
  production build and production dependency audit passed. Existing large-OpenAPI-file and
  dependency-baseline warnings remain. No frontend behavior was changed.
- License generation added only the new desktop dependency family, with no existing entries
  changed or removed. Regeneration was byte-stable. `licenses:check` correctly reports the
  generated file's uncommitted diff against HEAD; it is not recorded as a green check.
- Documentation inventory and whitespace checks passed. Initial compiler/analyzer and two
  constructor-convention failures were corrected without weakening any gate.

### Native smoke: supplemental WSLg evidence

- The actual WebKitGTK adapter loaded the production React bundle from the isolated engine's
  HTTP `127.0.0.1` origin, not Vite. The operator confirmed rendering, keyboard input and resize.
- The operator confirmed sign-in survived closing and reopening the native window with the
  same profile and still-running engine. Authentication flags and origin guards were unchanged.
- Diagnostics reported a secure context and microphone, AudioContext, AudioWorklet, WebSocket
  and clipboard API presence. Actual permissioned operations and realtime delivery were not
  tested. `cookieManager=False` describes the optional managed wrapper, not browser cookie
  persistence; the manual persistence check passed despite that value.
- Each manually closed window exited successfully. The attached engine remained running
  across window closes, then exited successfully on the smoke supervisor's termination signal.
  Process-group checks found no remaining members. No model was loaded.
- The session had no StatusNotifierWatcher tray host. EGL/DRI3 warnings mean accelerated
  rendering is not established by this smoke.
- The first engine attempt failed to bind the optional container bridge's shared default
  endpoint. Later runs disabled `ContainerBridge` and `ExternalApps` only in the scratch
  environment. Container functionality was not validated; no engine bind policy was changed.

Raw local evidence is under `.tmp/phase0-backend-gate-final.log`,
`.tmp/backend-test-results/`, `.tmp/phase0-frontend-*.log` and `.tmp/native-probe/` in the
implementation worktree. Runtime data and browser profiles are private, ignored artifacts,
not commit inputs.

### Functional follow-up — supplemental WSLg evidence

- **Download failed:** the operator created a scratch benchmark project and clicked
  Export JSON (v4); no download or visible response occurred. No matching export was found
  in the checked Linux Downloads directory or worktree root. The exact failure point is
  not yet established: `DownloadBlob.ts` / `saveBlob` uses an object URL, while
  `DesktopLaunchOptions.ClassifyNavigation` blocks top-level `blob:` navigation. Whether
  this adapter reports that download as navigation still needs runtime tracing; changing
  the navigation allow-list without proving safe download handling is not an accepted fix.
- **Upload passed:** the operator selected the disposable `upload-check.txt` through the
  native file picker and confirmed its Documents row. Engine logs show ingestion reached
  the missing-embedding-model failure. This proves file selection/upload, not indexing;
  the isolated engine has no embedding model installed.
- **Clipboard passed:** the operator used the local model proxy's Copy control and
  confirmed that the pasted scratch key matched in an external text editor. No key was
  captured in evidence. Revocation was requested; no credential is a commit input.
- **External link handoff passed:** About → Licenses → `@mantine/core` opened the normal
  browser. The initially suggested Application → Repository control does not exist in
  this build because `AboutData.ts` / `applicationInfo` supplies no repository URL.
  This does not establish OAuth callback completion or keyboard/accessibility acceptance.
- Actual microphone/worklet capture and controlled SignalR delivery/reconnect remain
  untested. No product code or authentication/navigation policy was changed for these checks.
- The ten-minute manual-check deadline expired: the supervisor terminated the native
  window (exit -15), then the engine exited successfully (exit 0). Both process groups
  were empty afterwards. This round is not evidence of a manual-close/graceful-window-exit
  pass; only the individually confirmed functional checks above are recorded as passes.

Raw local logs: `.tmp/native-probe/engine-functional.log` and
`.tmp/native-probe/desktop-functional.log`. These are isolated WSLg observations, not passes
for the supported Windows and Ubuntu desktop/session matrix.

### Diagnostic follow-up — supplemental WSLg evidence

A disposable copy of the shell, outside product source and solution enrollment, added
scheme/disposition traces and aggregate XHR, blob-anchor and WebSocket event counters.
It used the production React bundle and the same isolated data/profile. No headers,
credentials, message bodies, audio or device identifiers were logged. Navigation policy
remained unchanged. The diagnostic copy passed a fresh Release build with zero warnings
and errors; scratch build-server, package-pin and analyzer failures were resolved before
running it. No production implementation fix was made.

- **Download rejection confirmed:** six benchmark exports returned HTTP 200 and produced
  six blob-anchor clicks; all six reached `DesktopWindow.OnNavigationStarted` with
  `scheme=blob` and `disposition=Blocked`. The operator again observed no download.
  This establishes the shell's rejection point, not that merely allowing blob navigation
  would provide complete or safe download handling. Genuine downloads must be distinguished
  from top-level executable blob documents before changing the production policy.
- **Microphone check failed:** the operator reported immediate completion after Start
  capture, showing "No transcript yet". Read-only session metadata showed two Microphone
  sessions, each Completed with zero duration and no persisted error fields. The operator
  confirmed manually downloading the small transcription model on first visiting the page;
  it was not an automatic download caused by the test instructions. No successful capture,
  AudioWorklet execution or inference was established. A sandbox GPU query failed and its
  escalated retry was interrupted, so no further model-execution test was attempted.
- **Capture error visibility defect identified:** `useLiveCapture.start` calls teardown
  before retaining a capture-start error; teardown normally ends the empty live session.
  `TranscriptionSessionPage` then stops rendering `CaptureControls`, which contains that
  error. This can conceal permission/device/worklet failures behind a completed empty
  transcript. The actual browser rejection remains untraced. Missing GTK permission
  handling is a hypothesis: WebKit denies unhandled user-media requests by default
  ([WebKit documentation](https://webkitgtk.org/reference/webkit2gtk/stable/class.UserMediaPermissionRequest.html)).
- **SignalR delivery passed:** with Scheduler visible in the untouched native window,
  the operator created a Manual `signalr-before` job in a separate authenticated browser
  on the same origin. It appeared natively without refresh/refocus. `useScheduledJobs`
  does not poll, and the browser's mutation cache is separate. Native WebSocket opens
  and incoming messages were observed, not just API presence or fallback transport.
- **Short-outage reconnect passed:** the supervisor gracefully restarted only its scratch
  engine on the identical origin; native sockets closed and reopened. A browser-created
  Manual `signalr-after` job then appeared in the untouched native window. Only the initial
  top-level document load was logged. Neither job was intentionally run. This does not
  establish long-outage recovery or reconciliation of events missed during an outage.
- The operator closed the native window normally (exit 0); the attached engine was still
  alive. The supervisor then stopped it successfully (exit 0). Both engine generations
  and the desktop process group were empty afterwards. Test-key revocation remains
  unconfirmed.

Raw local evidence: `.tmp/native-probe/functional-domki8wp/`. The disposable diagnostic
source and binary remain under the probe worktree's ignored `.tmp/download-diagnostic/`;
neither is part of the production diff.

### Media diagnostic follow-up — supplemental WSLg evidence

- The operator again observed immediate completion after Start capture in the native
  window, while a normal-browser comparison opened the microphone dialog and worked.
  The disposable diagnostic reported installed media observers but **zero observed
  `getUserMedia` and `AudioWorklet.addModule` calls** throughout. This does not establish
  a permission rejection: a pre-acquisition failure or an instrumentation limitation
  still needs to be distinguished. No permission grant or production capture fix was added.
- Scratch session metadata included a zero-duration completed microphone session and a
  later completed session with 1,750 ms of audio. The latter is consistent with the
  browser comparison, but metadata alone does not identify the originating client.
  The engine started the small Whisper runtime; native capture/worklet/inference success
  remains unproved. Resource headroom was checked before this round.
- The operator reported intermittent scrolling flicker in the first third of Node
  Settings and also on Transcription. WSLg/compositor involvement is a hypothesis, not
  a diagnosis. A same-page browser comparison and real Ubuntu session reproduction
  remain required; no rendering workaround was applied.
- Download investigation found no navigation-action download-attribute accessor in the
  installed WebKitGTK exports or its documented API. The proposed native-only filter
  cannot rely on that nonexistent API. A narrowly scoped native save bridge is an
  experiment candidate, not an implemented fix; all blob navigation remains blocked.
- The seven-minute deadline expired: desktop exit -15, engine exit 0. The supervisor
  reported empty desktop/engine process groups; a separate process check also confirmed
  the Whisper child's group was empty. This is not a manual-close acceptance pass.

Raw local evidence: `.tmp/native-probe/functional-px32raw_/`; disposable diagnostic
source is in the probe worktree's `.tmp/download-media-diagnostic/`. Its fresh Release
build passed with zero warnings and errors. Product source was unchanged in this round.

### Source-level capture tracing — supplemental WSLg evidence

A disposable Vite transform added synchronous numeric phase counters only to
`useLiveCapture.start` and its teardown paths. Product source was not edited. Before
opening the native window, the supervisor compared the served index and instrumented
transcription asset byte-for-byte with the diagnostic build. Both matched.

- **Permission failure established:** one Start capture reached `liveStartResolved=1`,
  `beforeMicrophoneStart=1`, `microphoneRejected=1` and `catchPermissionDenied=1`.
  Neither start guard returned; no generation mismatch, unmount, stop or abort preceded
  the failure. `MicrophoneCaptureSource.toMicrophoneCaptureError` maps only
  `NotAllowedError` and `SecurityError` to this result. The operator again saw immediate
  completion and confirmed that no permission prompt appeared.
- **Earlier observer inference invalidated:** the installed Web API wrapper still
  reported zero calls while the source-level trace proved acquisition was attempted.
  Its zero counters cannot establish that capture stopped before acquisition. The
  observer discrepancy remains unexplained; do not use it as evidence against a
  permission failure. No successful AudioWorklet execution or capture is established.
- The next permission experiment must require explicit consent and reject unrelated
  permissions; it must not silently allow all native requests. The exact native request
  and origin checks still need verification. WebKit documents default denial for
  unhandled user-media requests
  ([reference](https://webkitgtk.org/reference/webkit2gtk/stable/class.UserMediaPermissionRequest.html)).
- Transform assertions and TypeScript syntax checks passed. The frontend diagnostic
  build passed; the copied desktop passed a fresh Release build with zero warnings and
  errors. The `pnpm` launcher hung even for `--version`; it was stopped with no remaining
  processes, and the installed Vite entry point succeeded through Node. No dependency
  or product configuration was changed to work around the launcher.
- The operator closed the window normally (exit 0); its attached engine was still alive.
  The supervisor then stopped the engine (exit 0), and both process groups were empty.
  No Whisper process was spawned during this round.

Raw local evidence: `.tmp/native-probe/functional-bl1zzav4/`; disposable frontend transform,
webroot and shell are in the probe worktree's `.tmp/microphone-phase-diagnostic/`.

**Permission implementation checkpoint:** the pinned GTK adapter has no managed permission
event and does not connect WebKit's `permission-request` signal. Installed native APIs
identify audio/video/display requests but do not expose the requesting frame's origin.
The top-level WebView URI alone is not proof of the requester's origin; upstream GLib
[implementation](https://raw.githubusercontent.com/WebKit/WebKit/main/Source/WebKit/UIProcess/API/glib/WebKitUserMediaPermissionRequest.cpp)
currently discards the requester and top-level origin parameters. Do not turn a top-level
URI check into a blanket microphone grant. A consent prototype needs independently enforced
frame restrictions in the disposable host/page, audio-only filtering, navigation/close
cancellation and explicit Allow/Deny controls. The restricted scratch experiment below
implements these constraints; it does not establish a production permission policy.

### Restricted microphone consent prototype — supplemental WSLg evidence

The operator approved a scratch-only audio consent experiment. A disposable hosting-startup
wrapper invokes the actual engine entry point and adds exact response restrictions:
`Content-Security-Policy: frame-src 'none'; object-src 'none'` and
`Permissions-Policy: microphone=(self), camera=(), display-capture=()`.
The isolated data/profile and diagnostic frontend are unchanged; no production policy changed.

- The native GTK handler offers explicit Allow/Deny for audio-only user-media requests,
  rejects camera/display requests, defaults to Deny and times out after 30 seconds.
  Pending requests are invalidated on navigation or close. Non-user-media permissions
  retain WebKit's default handling; this is not a general permission framework.
- Arming requires exact headers plus observed same-origin and cross-origin frame blocking.
  Page-side frame diagnostics are evidence for this trusted scratch page, not an
  adversarial security attestation. Independently enforced host headers are the boundary.
- Both scratch Release builds passed with zero warnings/errors. The actual HTTP host
  header self-check passed, and the native policy/lease/header self-check passed 13/13.
  Coordinator review covered generation invalidation, prompt cleanup, native callback
  lifetime, and close/initialization races.
- Earlier preflight attempts failed closed: a test customization bypassed normal desktop
  initialization; the wrapper now uses the actual entry point. A slim-builder self-test
  did not discover hosting startup; it now uses the engine's full builder. Two native
  preflights exposed canceled frame-navigation timing; document generations now track
  only allowed document navigation while every attempt invalidates pending permission.
  Those windows were supervisor-stopped before user testing, not manual-close passes.
- In `functional-6lczi_j7`, served index and phase asset matched the diagnostic build;
  the hook attached and header/frame/current-document verification all passed. The
  operator saw the dialog and selected Allow. Native counters recorded one prompt and
  one allow; source counters recorded successful microphone startup, and the AudioWorklet
  module loaded successfully. The previous unhandled-permission blocker was bypassed
  with explicit consent, without a blanket grant.
- **End-to-end transcription still failed:** Whisper returned HTTP 500, followed by
  `WhisperRuntimeException` and repeated `LiveSegmenterStalledException` failures. The
  operator saw “The live transcription lane stopped making progress.” Audio reached
  the backend submission path, but successful recognition and the underlying reason
  for the runtime rejection remain unproved. No fix or model download was attempted.
- A second capture attempt also resolved microphone startup and loaded the worklet, but
  native totals stayed at one request/prompt/allow. This is consistent with reuse of the
  earlier WebKit grant; a new transcription session did not provide a fresh Deny test.
  **Deny remains manually untested**, as do pending-prompt navigation/close cancellation
  and timeout. Gate self-checks are not substitutes for those runtime checks.
- The operator closed the window normally (exit 0), leaving the attached engine alive.
  The supervisor then stopped the engine (exit 0); desktop, engine and the separate
  Whisper process group were all empty. No production policy or capture code changed.

Disposable sources: integration worktree `.tmp/native-probe/restricted-host/` and probe
worktree `.tmp/microphone-consent-diagnostic/`. The prototype is not part of the shipped
source diff and must not be treated as Windows or real Ubuntu acceptance.

### Fresh-process Deny check — supplemental WSLg evidence

The operator approved reopening the same restricted scratch prototype. No source or
binary changes were made. In `functional-51hwboew`, the supervisor again verified the
served diagnostic assets and restrictive headers; the native hook and document/frame
verification passed before manual testing.

- Native counters recorded one request, one prompt, one Deny, zero Allow and zero
  timeout/cancellation. Source counters recorded one microphone rejection and one
  permission-denied catch, zero successful microphone startup and zero worklet loads.
  This establishes the Deny path in the fresh process. The operator confirmed the
  dialog appeared, but after Deny the UI showed only “no transcription yet” and
  Completed. Permission enforcement worked; denial was not clearly surfaced to the
  user. This reproduces the earlier error-visibility defect, not a successful session.
- The window closed normally (exit 0), with the attached engine still alive. The
  supervisor stopped the engine (exit 0); both process groups were empty and no Whisper
  child was recorded. No additional model or runtime was installed.
- Together with the earlier Allow round, the scratch consent decision paths now have
  runtime evidence. Pending-prompt navigation/close cancellation, timeout and actual
  camera/display rejection remain runtime acceptance gaps. The existing gate self-checks
  do not replace them. Whisper HTTP 500 and end-to-end transcription remain unresolved.

### Synthetic Whisper isolation — no microphone input

The operator approved synthetic-audio diagnosis independently of the frontend error-visibility
fix. Two scratch harness runs invoked the already-installed b5130 CPU `whisper-server`, small
model and Silero VAD, using the production argument shape (loopback, eight CPU threads, no
conversion) and multipart field names. Requests contained in-memory 16 kHz mono PCM16 silence;
no microphone, user recording, model download or production setting change was involved.
This directly tests the daemon protocol, not the application supervisor/provider lifecycle.

| Fresh daemon sequence | HTTP result |
| --- | --- |
| 1 s silence, VAD on, language probabilities on | 500 |
| 5 s silence, VAD on, language probabilities on | 500 |
| 1 s silence, VAD off, language probabilities on | 200 |
| 5 s silence, VAD off, language probabilities on | 200 |
| 1 s silence, VAD on, language probabilities off | 200 |

A second fresh process checked ordering: VAD on/probabilities off returned 200, then VAD
on/probabilities on returned 500 with the fixed error classification
`basic_string::_M_construct null not valid`. A VAD-off request returned 200; repeating VAD
on/probabilities on afterwards also returned 200. Warm state can therefore mask this failure.
The daemon trace shows zero VAD speech segments followed by unknown language ID -2 in failing
requests. The [pinned server source](https://raw.githubusercontent.com/ggml-org/whisper.cpp/b5130/examples/server/server.cpp)
uses the language-detection return value without checking for a negative error before
constructing the verbose response; its exception handler converts exceptions to HTTP 500.
This establishes a synthetic failure mechanism consistent with the earlier live failure,
not proof of the contents of the operator's audio. HTTP 200 on silence is not recognition
accuracy evidence, nor proof that warm-state language metadata is valid.

Both bounded runs exited normally after supervisor SIGTERM, with empty process groups.
Evidence: `.tmp/native-probe/synthetic-whisper-xiaz1m15/` and
`.tmp/native-probe/synthetic-whisper-pijmly7t/`; ignored harnesses are `synthetic-whisper.py`
and `synthetic-whisper-order.py`. Only counters, fixed error classification and synthetic
runtime diagnostics were retained; successful recognition response text was not printed.
Do not disable VAD/probabilities globally or retry all HTTP 500 responses as a speculative
fix. Choosing a runtime fix or a narrowly justified compatibility change is a separate step.

### Capture error visibility fix — isolated frontend branch

The approved production fix is isolated in `.tmp/worktrees/capture-error-visibility`,
branch `fix/transcription-capture-error`, based on `develop` at `b5eccedd7`.
Only `TranscriptionSessionPage.tsx` and its test file changed. `TranscriptionSessionContent`
keeps the existing localized capture alert visible after a live session becomes terminal,
without offering Start/Stop on a terminal session; a session key resets capture state on
route changes. The hook's resource cleanup and all REST/SignalR/persistence contracts are
unchanged. Completed remains the persisted status, and the local error is not durable
across reloads. This fixes error visibility, not persisted failure semantics.

Coordinator review checked both changed files, terminal controls, session isolation and
unrelated file-session behavior. Final validation on the frozen source passed:

- Frozen dependency installation; `pnpm run validate` (existing OpenAPI-size warning and
  dependency baseline warnings, no errors).
- `pnpm run test:coverage:check`: 465 files, 4,728 tests passed; coverage thresholds passed.
- `pnpm run test:tooling`: 67 tests passed; production dependency audit reported no known
  vulnerabilities.
- `pnpm run build`, including typecheck, bundle budget and exact frontend license corpus.
- `git diff --check`; only the two intended source/test files were modified, index empty.

The initial worker install was mistakenly invoked at repository root and failed without a
lockfile; its task-created empty root package scaffold was removed. Sandbox package-manager
identity lookup failed, and a later silent worker bootstrap was terminated with all owned
process groups verified empty. The final gates above ran outside the sandbox with normal
pinned-package-manager verification; no checks were disabled. Earlier focused-test evidence
is superseded by the coordinator's complete coverage run. Backend gates were not rerun for
this frontend-only change.

In native round `functional-zyrlahba`, the scratch host served the fix branch's production
`dist`, not the earlier phase-instrumented bundle. The supervisor compared served index and
`transcription._sessionId-CaWbp23j.js` byte-for-byte with that build and verified restrictive
headers before opening the window at `http://127.0.0.1:35207`. The native hook/frame checks
passed; native counters recorded one prompt and one Deny, zero Allow. The operator confirmed
that “Access was refused. Allow the microphone or screen share and start again.” remained
visible after completion. The window closed normally (exit 0), the attached engine remained
alive, and the supervisor stopped it (exit 0); both process groups were empty. No Whisper
child was recorded. This is supplemental WSLg UI evidence, not the supported-platform matrix.
No commit, cherry-pick or integration into `develop` has occurred.

### Scratch patched-runtime proof — CPU, XE request shape only

The operator approved downloading/building the pinned source and testing a scratch patch;
no production pin, installed binary, application source or permission setting was changed.
The shallow b5130 checkout resolved to commit
`927cfce34f31707e17f2bff35c349632fb9e2c3a`. Both unmodified and patched CPU Release builds
completed without compiler warnings. Build directories and `server-silence.patch` are under
probe worktree `.tmp/whisper-silence-proof/`; only upstream `examples/server/server.cpp`
was edited. The baseline executable hash was rechecked unchanged after the patched build.

The patch reads current segment count before constructing verbose JSON, emits unknown
language for an empty result, avoids a language-probability pass on empty current segments,
and checks both language IDs before conversion/indexing. It does not retry inference or
disable VAD. Coordinator source review confirmed these guards and the scope limitations below.

Coordinator acceptance used generated 1 s/5 s in-memory silence and the pinned project's
bundled 11 s JFK speech fixture (16 kHz mono PCM16), not microphone or personal recordings.
Both binaries received the same six-case sequence through the existing multipart shape:

| Case | Unmodified source build | Patched source build |
| --- | --- | --- |
| Fresh 1 s silence | HTTP 500, expected negative control | HTTP 200, empty transcript, unknown language |
| Fresh 5 s silence | HTTP 500, expected negative control | HTTP 200, empty transcript, unknown language |
| Reference speech | HTTP 200, 3 segments, English probabilities | HTTP 200, 3 segments, English probabilities |
| Silence after speech | HTTP 200, stale English metadata | HTTP 200, empty transcript, no stale metadata |
| Reference speech again | HTTP 200, 3 segments | HTTP 200, 3 segments |
| Malformed non-WAV data | HTTP 400 | HTTP 400 |

Both harness modes passed their six explicit expectations; the baseline's expected HTTP
500s are failure reproduction, not a working-runtime claim. Speech assertions required
nonempty segments, a known fixture word and available English probabilities; no broad
recognition-accuracy claim follows. Both daemons exited 0 after supervised shutdown and
process-group checks were empty. No source-level exception guard for an invalid ID with
nonempty segments was deliberately fault-injected.

Evidence: integration worktree `.tmp/native-probe/patched-whisper-bl5gbl5i/` (baseline)
and `patched-whisper-7rmvx0ne/` (patched); reusable ignored harness
`patched-whisper-check.py`. Executable SHA256:
- Baseline: `7a63617f815c0e51237ec13c151e64f263ebac5e40eae67b9172155174a41f8d`.
- Patched: `ebf71c2166850f2de069da8f09977cb021315bee8e460ef67d96a165e0fa7373`.
- Speech fixture: `59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e`.

**Not a production-ready general upstream patch:** tested with one processor and XE's
transcription request shape (`verbose_json`, VAD on, probability reporting requested,
language-only `detect_language` mode off). The multi-processor empty-VAD branch can retain
old segments, and language-only detection has distinct semantics; both are outside this
patch/proof. Windows, GPU backends, packaging/update provenance and end-to-end XE live
transcription with this binary remain unvalidated. Choosing how to obtain/distribute a
corrected runtime requires a separately approved production plan. A read-only readiness
lookup failed before the worker created its scratch directory; subsequent clone/build/test
operations succeeded and no production file was touched to repair it.

### Patched runtime through XE live transcription — isolated fixture check

The operator approved continuing the scratch proof through the real XE path. The coordinator
launched the existing Release `Client` entry point (no test factory or transcriber mock),
with process-only `XE_WHISPERCPP_SERVER_PATH` pointing to the reviewed scratch patched
binary and `XE_WHISPERCPP_BACKEND=cpu`. Both `XE_DATA_DIR` and `XDG_DATA_HOME` remained in
native-probe scratch space. The supervisor verified the executable hash before startup and
verified `/proc/<child>/exe` against the patched path after XE spawned its child. No production
settings, pins or binaries were replaced, and no microphone/browser was opened.

A disposable Node client used the installed SignalR client and actual scratch-account login:
create session → subscribe → REST live/start → Mono PCM frames → hub EndSession → durable
REST readback. All requests were bounded; 250 ms frames were paced every 500 ms, with one
hub invocation in flight. This deliberately conservative admission rate is not real-time
throughput or load evidence. Event handlers were registered, but event payload correctness
was not asserted; the evidence is real hub ingress and persisted results, not UI projection.

The first run (`engine-whisper-bk5xujhr`) failed before runtime startup: the scratch client
sent a JSON Content-Type on bodyless start/cancel requests, and both returned HTTP 400.
The client exited 1, engine exited 0, and both groups were empty; no Whisper child was
started. Its newly created scratch session was not successfully canceled. The route source
explicitly requires no body, so the client was corrected to omit Content-Type when bodyless.
A reviewed misspelled status-event name was also corrected before runtime execution.

Coordinator acceptance run `engine-whisper-l5_0j5rs` passed all three cases:

| Case | Persisted result |
| --- | --- |
| Cold runtime, 6 s generated silence | Completed; duration 6,000 ms; 0 segments; language null |
| 11 s bundled JFK fixture + 6 s silence | Completed; duration 17,000 ms; 3 segments; language en |
| New 6 s silence session on the warm runtime | Completed; duration 6,000 ms; 0 segments; language null |

Assertions checked null error fields, exact duration, segment count/sequence/channel,
reference speech content, and no segment starting in the trailing-silence interval. The
engine log contained neither Whisper request rejection nor lane failure. Client and engine
exited 0; engine, client and the separately contained Whisper process group were empty after
supervised shutdown. No successful transcript text or authentication token was logged by the
client. Harness syntax checks and coordinator review passed; docs inventory and whitespace
checks passed. No .NET or frontend production code changed in this round, so their full
gates were not rerun.

Ignored harnesses: probe worktree `.tmp/whisper-engine-check/client.mjs` and integration
worktree `.tmp/native-probe/patched-whisper-engine-round.py`. This proves the provider,
segmenter, real hub ingress and persistence path for the CPU fixture cases; patched-runtime
microphone/WebView capture, normal-rate load, supported-platform coverage and production
runtime delivery remain separate acceptance work.

### Native microphone with patched scratch runtime — supplemental WSLg pass

The operator approved the next native microphone check. The restricted scratch host used
the reviewed patched CPU runtime through process-only override variables and the validated
capture-error branch's production frontend bundle. The supervisor verified the binary hash,
served index/session asset bytes and restricted headers; native frame checks passed. It also
verified the actual XE-spawned child's executable path against the patched build.

In `functional-j9ystjsg`, the native handler recorded one prompt and one explicit Allow;
AudioWorklet module loading resolved successfully. The operator tested initial silence,
a spoken desktop-test phrase, trailing silence and Stop, then confirmed normal capture
behavior and a response containing the spoken words without an error. Read-only scratch
session metadata showed 15,500 ms duration and null error code. Neither a Whisper request
rejection nor a live-lane failure appeared in this run's engine log. The earlier generic
getUserMedia wrapper still reported zero calls and remains unsuitable as negative evidence;
no new claim relies on it. Transcript/audio content was not extracted into diagnostic logs.

The window closed normally (exit 0), leaving the attached engine alive. The supervisor
stopped the engine (exit 0), and desktop, engine and Whisper process groups were all empty.
The launcher is `.tmp/native-probe/patched-microphone-round.py`; no product source, installed
runtime or production setting changed in this round. Documentation inventory and whitespace
checks passed. This is a short supplemental WSLg native capture-to-transcript pass, not a
sustained-load, Windows, real Ubuntu X11/Wayland or production-packaging acceptance pass.
The microphone blockers now have an isolated working combination; production permission
policy and corrected runtime delivery still require their own approved implementation.

### Native blob export — restricted scratch Save/Cancel proof

The operator approved a scratch-only save-intent bridge, not a production frontend or
navigation-policy change. The isolated GTK prototype uses WebKit's native downloader and
Save/Cancel dialog. Ordinary blob navigation remains blocked. The bridge transmits a blob
URL, suggested filename and integrity metadata, never file bytes, bearer tokens or a
JavaScript-selected destination. It retains the object URL until terminal acknowledgment
or timeout. The shell checks the restricted current document, nonce, one-use request ID,
blob origin and filename; only one transfer is admitted, with a bounded per-document ID
history. Other downloads from this WebView are canceled by a native context hook.

Coordinator review found and corrected timeout cleanup when the message channel throws,
missing native unmatched-download guarding, completed-request replay admission and async
guard-publication races. Release analyzer failures were corrected without disabling rules;
scoped formatting and compiler-server shutdown also required execution outside the sandbox
after named-pipe/shutdown failures. Final coordinator validation rebuilt the frozen scratch
project with Release and `--no-incremental`: 0 warnings and 0 errors. Save parser checks
passed 9/9, retained consent checks 13/13, and the exact injected JavaScript passed 11/11
coordinator checks, including terminal matching, timeout/closed-channel release, ordinary
anchor fallback, oversize refusal and navigation-before-hash cleanup.

In `functional-9jxv8uu7`, the supervisor verified the same production frontend bundle and
restricted headers used for the capture-error check. Runtime frame restrictions and bridge
installation passed. Benchmark JSON export returned HTTP 200, the native Save dialog
appeared, and the operator saved a fresh file. The coordinator independently read that
file: 1,210 bytes, valid JSON object, SHA-256
`7d7373a8c624884b3d3e557165363f410603b7d4fd81a5cb546e5daf1648882c`, matching
the original in-page blob. File contents were not logged. A second export reached the
dialog and was canceled; native cancellation recorded `saved=False`, with no second
successful save. The operator confirmed XE remained usable. The window exited 0, leaving
its attached engine alive; supervised engine shutdown exited 0, with both groups empty.

Implementation and runnable checks remain ignored scratch artifacts:
probe worktree `.tmp/download-save-prototype/` and integration worktree
`.tmp/native-probe/download-bridge-check.mjs`. Existing frontend download helpers and
production shell code were not changed. This deliberately refuses overwrite, limits the
declared blob size to 50 MiB and admits at most 128 request IDs per loaded document.
It relies on a trusted, frame-restricted diagnostic document, not adversarial origin
attestation. Windows, real Ubuntu X11/Wayland, overwrite UX, all other export paths,
native pending-download navigation/close/timeout and unmatched-download negative controls
remain unvalidated. A production shared download seam requires a separate approved plan;
do not ship the diagnostic prototype wholesale.

### Scrolling flicker — reversible WSLg compositing comparison

The operator approved a baseline/compositing-disabled/restored-baseline comparison.
No frontend, shell source, installed package or persistent setting changed. All rounds
used the same existing scratch desktop binary, profile, restricted host, origin and
production frontend bundle. The supervisor verified served index/session asset bytes
and restricted headers each time; no model operation was requested.

| Round | Scratch evidence | Operator observation |
| --- | --- | --- |
| Normal rendering | `functional-0amf7gzr` | Flicker while scrolling near the top of Node Settings in native XE; normal browser comparison did not flicker |
| `WEBKIT_DISABLE_COMPOSITING_MODE=1` | `functional-j2j_z4_t` | No more flicker in the same native section |
| Both compositing overrides removed | `functional-hb8ptqur` | Flicker returned on the second upward scroll |

The coordinator verified the disabled-round variable and absence of the force override
in the owned desktop process environment, then verified both overrides absent in the
restored round. The switch is read by the installed-version upstream
[`HardwareAccelerationManager`](https://raw.githubusercontent.com/WebKit/WebKit/webkitgtk-2.52.6/Source/WebKit/UIProcess/gtk/HardwareAccelerationManager.cpp).
This establishes sensitivity to WebKit's compositing path and a reversible workaround
in this WSLg environment. It does not identify the underlying driver/compositor defect,
prove an Avalonia or React bug, or establish behavior on supported Ubuntu sessions.
EGL/DRI3 warnings persisted in all three rounds, including the non-flickering one; their
presence alone therefore does not diagnose the observed flicker.

Read-only UI inspection found a shared Framer Motion layout-projection wrapper enclosing
the `#main-content` scroll container (`Layout.tsx`, `Layout`) and rounded, clipped Mantine
cards (`SectionCard.tsx`, `SectionCard`). Neither affected page uses list virtualization.
No CSS workaround was applied, and the comparison does not establish that those layout
structures are defective. This round specifically reproduced Node Settings; the earlier
Transcription flicker report has not received its own equivalent comparison.

All three native windows exited 0 and left their attached engines alive; supervised
engine shutdowns exited 0 and every owned desktop/engine group was empty. No production
build/test gates were rerun because no product source changed. Documentation inventory
and whitespace validation passed. Keep normal rendering as the default pending real
Ubuntu X11/Wayland evidence; a user-selectable compatibility mode is an implementation
option, not an approved or implemented production change. Rendering throughput, CPU
cost, media behavior and heavy-inference interaction with compositing disabled remain
unmeasured.

### Windows portable probe — startup blocked before WebView2

The operator confirmed that Windows is the host of this WSL checkout and approved a
self-contained Windows shell connected to the isolated WSL engine. Real Ubuntu session
testing was explicitly deferred by the operator; it remains unvalidated, not passed.
This Windows round does not establish Windows engine, runtime or installer behavior.

Read-only Windows checks found WebView2 installed but no .NET 10 shared runtime. A
portable `win-x64` Release publish therefore included its own .NET runtime. Coordinator
Release rebuild reported 0 warnings/errors and produced an identical DLL to the publish.
All copied files were hash-verified in a unique Windows Temp directory. The executable
successfully ran its expected invalid-argument exit without opening a GUI. An initial
asset check incorrectly required a standalone `WebView2Loader.dll`; inspection of the
pinned Avalonia loader showed it loads the installed runtime directly. No loader package
or system runtime was installed to repair that check.

A scratch PowerShell 5.1 supervisor creates its worker suspended, assigns it to a
kill-on-close Windows Job Object, then resumes it so the native shell and WebView children
inherit containment. Initial validator quoting failures and a PowerShell scalar `.Count`
failure were corrected. The first GUI launch exposed a second supervisor defect: managed
process exit-code reads returned null. Retained native handles and `GetExitCodeProcess`
replaced those reads. The coordinator's Windows self-check passed 4/4, including a real
no-GUI child exiting 7 and Job Object cleanup to zero active processes. The Linux wrapper
now rejects missing, null and nonzero shell result codes rather than trusting wrapper exit 0.

Windows reached the exact scratch SPA index over loopback in both attempts, verified by
SHA-256. However, `windows-p_qmhd3g` failed before adapter initialization. A scratch-only
startup exception diagnostic in `windows-w4ngvggs` identified
`Win32NativeControlHost.DumbWindow`: unable to create the native child window, with an
explicit suggestion that the supported-OS application manifest is required. Inspection of
the published EXE found only the default asInvoker manifest, without the compatibility
section included in [Avalonia's desktop template](https://raw.githubusercontent.com/AvaloniaUI/avalonia-dotnet-templates/master/templates/csharp/app/app.manifest).
Adding that manifest is the next minimal experiment; it has not yet been approved or
implemented, and the cause is not considered resolved before a successful retry.

The first round's Job Object cleanup verified zero active processes; its null exit status
is not a pass. The second diagnostic was stopped after capturing the exception, with owned
Windows processes verified absent and its WSL engine exiting 0. All owned WSL groups were
also verified empty. No Windows functional check passed. Portable bundles, diagnostic
source, supervisors, isolated profile and logs remain scratch artifacts; no installation,
persistent execution-policy change, production shell edit or integration into develop
occurred. Documentation inventory and whitespace checks passed.

### Windows manifest correction — first native rendering/download pass

The operator approved the scratch manifest correction. Only the copied Windows project
was wired to a new `app.manifest`: the Avalonia-template Windows compatibility GUID,
with explicit `asInvoker` and `uiAccess=false`. No elevation, navigation policy change,
package update or production source edit was introduced. The existing scratch startup
exception diagnostic remained in place. Release compilation reported 0 warnings/errors;
the coordinator reviewed the change, hash-verified the copied publish, and independently
used Windows resource APIs (`LoadLibraryEx` as data, RT_MANIFEST resource 1) to verify the
embedded compatibility and non-elevating execution declarations before launch.

In `windows-dimrjk8q`, the unchanged isolated WSL host served the same production frontend
bundle. Windows loopback returned the exact expected index hash. Unlike both prior
attempts, the native shell initialized WebView2, completed navigation, and produced no
stderr output. The operator confirmed rendering, typing, resizing and repeated scrolling
at the top of Node Settings worked as expected, without the WSLg flicker. This establishes
the manifest correction as sufficient to remove the observed startup blocker in this
Windows environment; it is not a claim about every native-host startup failure.

Benchmark JSON export also saved through Windows' existing WebView2 download behavior,
without the GTK-specific scratch save bridge. The coordinator resolved the Windows
Downloads known folder and inspected only the matching export written during this run:
one 1,210-byte valid JSON file, SHA-256
`3322029b04ed3fe9f49f696006b6675ca05b71a76198b8cd3afed09e6e8f5db2`.
Contents were not logged. Unlike the GTK bridge round, there was no in-page blob hash
instrumentation, so this is a real-file/JSON check, not byte-for-byte source comparison.

The operator closed the window normally. Native shell, Windows worker and supervisor
reported exit 0. Job cleanup verified zero active processes and absent worker/shell
identities; an independent Windows process check found no remaining owned processes.
The attached WSL engine stayed alive until supervised shutdown, then exited 0, with
both owned WSL process groups empty. Documentation inventory and whitespace checks passed.

Evidence remains in ignored `.tmp/windows-shell-probe/publish-manifest` in the probe
worktree and `.tmp/native-probe/windows-dimrjk8q` in the integration worktree, plus the
isolated Windows Temp profile/logs. The portable app was not installed, committed or
integrated into develop. Windows authentication persistence, upload/clipboard, microphone
permissions/capture, SignalR/reconnect, download cancellation and the full Windows engine,
runtime and packaging path remain separate checks. Real Ubuntu testing remains explicitly
deferred and unvalidated.

### Windows authentication, upload and clipboard follow-up

In `windows-qcyl1ery`, the operator confirmed the reused isolated WebView2 profile
remained signed in without entering credentials. The Knowledge Base file picker opened,
Cancel closed without an error, and the selected Windows scratch text file appeared in
Documents. This verifies selection/upload, not embedding or indexing.

The operator confirmed copying the scratch proxy **secret key** and pasting matching
text in Windows Notepad. This is a clipboard interaction pass; the requested base URL
copy was not exercised. No secret value was requested or recorded. Revocation was
requested but remains unconfirmed.

The native window closed normally with exit 0 while the attached engine remained alive.
The supervisor independently verified no owned Windows processes remained, then stopped
the WSL engine with exit 0 and verified both owned WSL process groups were empty.
Windows microphone and SignalR/reconnect checks have not yet run. This remains a
Windows shell connected to an isolated WSL engine, not full Windows deployment proof.

### Windows microphone — permission pass, sustained transcription failure

In `windows-fodqrxnn`, the operator confirmed the built-in WebView2 microphone
permission prompt appeared and Allow was selected. Initial spoken words were transcribed,
but capture then stopped with `live-overloaded` and the UI's could-not-keep-up message.
Sustained capture/transcription therefore failed; permission prompting and initial audio
transport passed. This used the isolated WSL engine and the hash-verified, previously
probed patched CPU Whisper runtime, not a Windows transcription runtime installation.

The first logged lane failure was `LiveSegmenterStalledException`: no progress across
two submissions ending at 7,000 ms. The same exception appeared 38 times. Inspection
of `LiveTranscriptionSegmenter.PushAsync` shows this guard counts repeated submission
boundaries, not elapsed processing time. Concurrent compiler CPU activity was observed
after the failure, but neither CPU contention nor a Windows-specific cause is established.
No fix or model change was attempted; Windows SignalR/reconnect was deferred.

The coordinator requested window closure and then used the supervisor stop control.
This is not a normal-close acceptance result: Windows launcher exit was 255 under
supervised termination. Independent verification found no owned Windows processes;
the WSL engine exited 0, both owned WSL groups were empty, and the separate Whisper
process group was empty. Scratch key revocation remains unconfirmed.

### Windows microphone diagnostic and realtime reconnection — supplemental pass

The cancellation correction was isolated in `fix/transcription-cancellation`: check the
caller token at `LiveTranscriptionSegmenter.PushAsync` entry, before accepting queued
audio or changing boundary state. An initial loop-top version failed the existing late-result
finalization test; moving the check to entry preserved that safety path. The corrected
Release rebuild reported zero warnings/errors, the focused Transcription suite passed
184 tests with five explicit skips, and the full backend gate passed: main runner 12,400
passes (batch counts may overlap), persistence 1,210 passes with one skip, agent tests
392 passes. Assembly guards passed. Three files remain uncommitted; no integration occurred.

A separate `test/transcription-diagnostic` worktree adds temporary metadata-only inference
and terminal-state logging. It is not part of the production fix. Its restricted host
passed a Release build and response-restriction self-check; the coordinator verified
application DLL SHA-256 `dfd6445f9a6845f1c76ffc9640bd4de9462b26abda43cfb5cb0cc81182cde71e`.
It reused only the existing isolated scratch data/profile and patched CPU Whisper runtime.
No audio or transcript content was added to diagnostic logs.

In `windows-n2wm142s`, the supervisor held the shared build lock for the full round;
preflight sampled 99.36% CPU idle. The operator confirmed the spoken words returned
correctly. All 19 inference submissions succeeded, including final flush; the winning
end reason was Completed and there were zero logged stall exceptions. At Stop, pending
audio was 88,000 bytes against a 320,000-byte budget and drained successfully. Individual
inference calls ranged approximately 43–2,490 ms. Some speech calls exceeded the 1,000 ms
tick interval, so this short successful round does not prove unlimited sustained speech
throughput. It also does not establish CPU contention as the cause of the earlier overload:
the earlier run lacked these timings, and the diagnostic uses a newer engine baseline.

The operator then created a Manual scheduler job in a normal browser and observed it in
the untouched native Windows Jobs list. After a controlled scratch-engine restart on the
same origin, a second browser-created Manual job appeared without native refresh/refocus.
Neither job was run. This confirms live updates and post-restart reconnection for this
Windows-shell/WSL-engine setup, not offline missed-event reconciliation or a full Windows
engine/runtime deployment.

The operator closed native XE normally; shell exit was zero and the attached engine stayed
alive until supervised shutdown. Both engine generations exited zero. Independent Windows
process verification found none remaining; all owned WSL groups and the separate Whisper
group were empty. Scratch proxy-key revocation remains unconfirmed. Real Ubuntu, packaged
lifecycle/tray/update behavior and sustained inference-load acceptance remain unvalidated.

### Windows sustained speech — measured backend overload

The same diagnostic build and patched CPU Whisper runtime were reused in
`windows-0_mllicf`, with the shared build lock reserved throughout the round and
99.03% CPU idle at preflight. The operator was asked for approximately 45 seconds of
continuous speech, but reported reaching about eight seconds before both the durable
`live-overloaded` error and capture overload message appeared. That user-observed value
is not treated as a measured capture duration.

The trace establishes the overload admission path: pending bytes equalled the
320,000-byte budget (10 seconds of mono PCM16 at 16 kHz); a further 8,000-byte frame
was refused. Thirteen successful inference calls consumed approximately 21,112 ms in
total; twelve exceeded the normal 1,000 ms tick interval, with a maximum of 3,013 ms.
The thirteenth submission ended at audio position 12,650 ms. The fourteenth call was
cancelled by the winning Overloaded termination. There were zero secondary
`LiveSegmenterStalledException` entries, consistent with the validated cancellation fix.

This is a sustained-throughput failure for the tested CPU runtime/model and overlapping
live-submission workload, not evidence of a missing Windows permission prompt or a
WebView rendering failure. The prior short phrase pass remains valid but does not imply
sustained realtime capacity. Increasing the queue alone would defer the failure and
increase latency, not establish processing capacity. Faster runtime/model execution or
less repeated inference needs separate measured evaluation; neither was changed here.

The operator closed native XE normally. Windows shell and WSL engine exited zero;
independent Windows process verification found no owned processes, all owned WSL groups
were empty, and the separate Whisper group was empty. No further model run or retry was
attempted. Sustained-speech acceptance remains failed; diagnostics remain scratch-only.

### Automated realtime comparison — baseline failure, comparisons pending

The approved scratch benchmark uses the existing public JFK WAV, monotonic real-time
frame deadlines, explicit pacing validity checks, durable sequence/duration checks,
and a separate conservative reference-phrase quality verdict. It distinguishes cold
single-clip testing from warm repeated-fixture testing. The client syntax check and
17 self-checks passed. Temporary cadence/thread controls remain only in the disposable
diagnostic worktree. Its Release rebuild reported zero warnings/errors and the restricted
host response self-check passed; production queue limits and settings were unchanged.

The first baseline attempt, `benchmark-f3j7fb4o`, was contaminated by competing compiler
and test activity despite holding the shared lock, so its timings are excluded. The
client also tried to cancel an already terminal session; its cleanup bookkeeping was
corrected to rely on durable terminal status, with added self-checks. The supervisor
independently verified all owned processes absent despite that client cleanup error.

The clean-load retry, `benchmark-jfeyfnxw`, verified the patched CPU runtime and eight
threads, with three preflight idle samples above 96%. At 1,000 ms cadence, the 11,000 ms
single fixture was sent in approximately 10,999 ms; maximum send lateness was below 3 ms,
with no late frames under the benchmark threshold. It failed durable completion with
`live-flush-failed`, not overload. The client exited 2, the engine exited zero, and all
owned process groups were verified empty. The sustained case was not reached.

The 2,000 ms/eight-thread comparison then failed low-load preflight (29.01% CPU idle)
before starting a model. Four-thread and sixteen-thread variants have not run. No
cadence or thread-count recommendation is established by these partial results. A
quiet interval is still required to complete the approved matrix.

### Two-second cadence — throughput pass, reference-quality checks failed

`benchmark-2heokfns` used the same patched CPU runtime/model with eight verified threads
and a 2,000 ms tick, under the shared lock. Three CPU-idle preflight samples were above
95%. The cold 11,000 ms clip and warm 55,750 ms repeated fixture both completed durably,
with correct duration/sequence checks, no late frames, and maximum send lateness below
1.1 ms. Stop-to-completion latency was approximately 3,627 ms and 2,306 ms respectively.
Both retained the reference ending phrase.

This is not an overall acceptance pass: strict reference checks failed in both cases.
The cold clip matched zero complete head phrases and one tail phrase against one
expected repetition; the sustained case matched three head phrases and four tail
phrases against five repetitions. Those checks identify recognition deviations, not
proof of lost audio. The client deliberately continued the throughput comparison but
returned exit 2 for quality failure. No tuning default was changed. A repeatable
quality comparison is required before recommending the slower cadence.

The engine exited zero and all owned process groups were verified empty. The next
four-thread/1,000 ms trial was blocked by the shared lock (exit 69), so neither that
trial nor the sixteen-thread variant has run. The clean-load one-second/eight-thread
baseline remains a flush failure; its lack of a completed transcript also prevents
attributing the observed recognition deviations specifically to the cadence change.

### CPU thread comparison — four-thread failure, sixteen-thread result unresolved

After one low-load preflight rejection before model startup, `benchmark-a383w0lu`
verified four CPU threads with the original 1,000 ms tick. Three idle samples exceeded
94%. The cold 11-second fixture had no late frames and sub-millisecond maximum send
lateness, but failed with `live-flush-failed`. The sustained case was not reached.

`benchmark-r_hdgf6n` verified sixteen threads at the original tick, with three idle
samples above 94%. The cold fixture passed throughput and strict reference checks;
Stop-to-completion latency was approximately 3,736 ms. The warm 55,750 ms fixture was
sent at realtime with no late frames and approximately 1.1 ms maximum lateness. Its
engine trace shows Completed requested, successful draining and a successful final
flush ending at 55,750 ms, but the client exited 2 on a benchmark assertion before
emitting its quality metrics. The client currently emits only a failure category at
that point; the exact assertion is therefore unresolved. This is NOT a sustained
acceptance pass and no cause is inferred from the missing assertion label.

Both supervisors verified all owned processes absent after shutdown. The operator
requested approval before responding to unexpected results, so work paused at this
unclassified validation failure. Proposed next step: add allowlisted check labels to
the scratch client and repeat only the unresolved variant, without changing runtime
settings or relaxing assertions. No production defaults were changed.

### Labelled sixteen-thread retry — sustained drain deadline failure

The scratch client's allowlisted failure labels passed 20 self-checks. In
`benchmark-ugucn1r4`, the unchanged 1,000 ms/sixteen-thread configuration ran under
the shared lock after three CPU-idle samples above 95%. The cold 11-second clip
passed throughput and strict reference checks, with approximately 3,909 ms finalization
latency. The warm 55,750 ms fixture had no late frames and maximum send lateness below
0.6 ms, but failed with `live-flush-failed` (`engine-terminal-failure`).

At sustained Stop, 184,000 bytes (5.75 seconds of mono audio) remained pending. The
engine logged that the lane did not drain within five seconds and was abandoned
unflushed. No overload admission was reported. This establishes a drain-deadline
failure in this retry; it does not retrospectively identify the earlier unlabelled
assertion. The short clip's identical transcript hash across both sixteen-thread runs
is only evidence for that short fixture, not sustained recognition quality.

The client exited 2, the engine exited zero, and all owned process groups were verified
empty. No runtime, cadence, queue, or drain-deadline change followed. The matrix has
no fully accepted sustained configuration: four/eight threads at 1,000 ms fail draining;
eight threads at 2,000 ms complete but fail strict reference checks; sixteen threads
at 1,000 ms have not produced a sustained acceptance pass. Further behavior changes
require a separately approved, evidence-driven direction.

### Still required before closing Phase 0

Windows WebView2 and real Ubuntu session coverage for the checks above; production-ready
download and media permission/runtime delivery, remaining negative controls and scrolling
flicker investigation; OAuth,
accessibility and missed-event reconciliation; packaged update/process-identity proof;
and approved real-model load validation. Supplemental WSLg successes do not replace the
supported-platform matrix. Engine ownership, activation and tray preference implementation
remain later, separately approved phases.


## Production completion checkpoint — 2026-09-22

The operator authorized completing the native desktop feature, including necessary fixes
and validation, without separate approval at each step. The original checkout stays
unchanged; integration is on `feat/native-desktop-completion`, based on `develop`
`109611616`. No merge is authorized. Backend modularization remains outside this scope.

### Remaining delivery slices

1. Shell-owned engine lifetime with explicit ownership, bounded readiness/shutdown,
   separately running engine attachment, and same-user second-instance activation.
2. First-close Keep running in tray / Quit / Cancel, remembered but editable preference,
   restore/settings/quit menu, and fail-visible behavior when no tray is available.
3. Production WebView security/permissions and Windows payload, prerequisite, launcher,
   and update/restart integration. Preserve browser, headless and operator CLI use.
4. Combined Release/backend/frontend/release-script gates, package smoke and native
   Windows acceptance; coordinator reviews the resulting diff and evidence before merge.

Real Ubuntu X11/Wayland acceptance was waived by the operator for this round. That is
a validation gap, not a Linux pass. WSLg observations remain supplemental. Manual Windows
checks must use an isolated profile/data root and provide explicit operator steps.

The operator subsequently confirmed that **native Ubuntu delivery remains required**;
the waiver covers real Ubuntu testing only, not implementation or packaging.

### Integrated validation checkpoint

The intermediate shell/hosting/update snapshot passed a non-incremental Release build
with zero warnings/errors and 112 focused tests with no skips under the assembly guard.
The release-script gate passed, including 118 PowerShell tests. Changed-scope Python
style, types, tests and security passed. The full frontend gate passed: 471 test files,
4,770 tests, coverage, tooling, production build and dependency audit. Documentation
inventory reported no missing entries. These are checkpoint results, not acceptance of
later changes or the full backend gate.

Review identified an explicit-port attachment mismatch and a pre-host bootstrap parent
monitoring gap; both require regression coverage before final acceptance. Production GTK
permission/download handling and final packaged lifecycle checks remain in progress.
The operator approved an explicit runtime-root setting for Windows scratch acceptance:
`XE_DATA_DIR` alone does not isolate provider recovery from the normal Windows user root.
No native Windows-owned engine is to be launched until that isolation is verified.

### Transcription evidence carried forward

The separate finalization change passed its backend and frontend gates before integration;
those results do not validate the combined desktop branch. Default eight-thread sustained
CPU transcription still overloaded. An isolated sixteen-thread run completed the public
55,750 ms fixture, but the old benchmark rejected four 20 ms timestamp overlaps. Existing
segmenter tests explicitly allow padded model timestamps across window boundaries.

The corrected scratch validator retains completion, sequence, channel, timestamp-range,
duration and reference-quality checks and reports overlaps diagnostically. Its 36
self-checks passed. Authenticated retrieval of the saved result through the normal API
confirmed 15 segments, all five expected head/tail phrase pairs and final-tail coverage;
four overlaps were 20 ms. No new inference was run for that revalidation and no production
thread default or queue limit changed. Both owned processes exited, and no Whisper launch
was observed. Evidence: ignored `saved-result-check-7xeulfhm` under the original desktop
worktree's scratch probe directory. This is not indefinite realtime or packaged Windows
acceptance, and the historical benchmark failure is not rewritten as an original pass.

### Integrated acceptance continuation — 2026-09-22

- The interrupted backend run and its overlapping retry are invalid evidence. Their surviving
  test process groups were explicitly terminated and absence of live members verified before
  the clean retry; no other checkout was stopped.
- Clean backend gate: Release build reported zero warnings/errors; main namespace batches
  reported 12,898 passes, persistence 1,216 passes plus one visible skip, agent tests 393 passes.
  All assembly guards passed. Architecture included 188 passing checks after correcting type
  placement, class syntax, real-pipe test categories and the exact desktop package allowlist.
- Subsequent Linux publish exposed an existing runtime-notice path concatenation assumption:
  `NuGetPackageRoot` without a trailing separator produced the wrong location. The two runtime
  pack paths now use `Path.Combine`; the real license-validation target passed with both root
  forms. This packaging-only correction followed the full gate above.
- Windows and Linux native payloads then published successfully with separate engine and shell
  input manifests. This is publish evidence, not final artifact-compliance or target-OS acceptance.
- A fresh Windows payload and isolated .NET runtime are staged for owned-engine testing using
  `XE_DATA_DIR` and `XE_RUNTIME_DATA_DIR`. Manual close/tray/activation and cleanup checks remain
  pending; real Ubuntu acceptance remains explicitly waived, not reported as passed.

### Windows owned-engine manual acceptance — 2026-09-22

The freshly published Windows-native payload started its own Windows engine with isolated
node, runtime, model and browser-profile folders (not a WSL-engine attachment).
The operator confirmed first-close Cancel, Keep in tray without remembering, tray Open,
second-instance restoration from minimized, and Quit. The second shell exited successfully
without changing the original engine PID. On Quit, the launcher exited zero, `ready.json`
was removed, and both acceptance supervisors reported zero remaining job processes with
verified cleanup. Remembered preferences, separately started-engine attachment, and final
artifact compliance remain outstanding.

Remembered tray behavior also passed: the next close hid directly without prompting; selecting
Ask every time in desktop settings restored the dialog. That owned-engine run exited zero
with verified cleanup. A subsequent native shell attached to an already-ready, separately
supervised Windows headless engine. The operator confirmed its attached-engine warning and
Quit. The shell exited zero; the original engine PID remained alive and `/health/ready`
returned HTTP 200/Healthy afterward. The standalone-engine supervisor then deliberately
reached its three-minute limit (exit 124), removed its job processes and verified cleanup;
this is bounded test teardown, not evidence of graceful standalone-engine shutdown.

Artifact corpus generation exposed two pre-existing dependency-pin drifts: the checker still
expected nuget-license 4.0.14 although the manifest pins 4.0.16, and Scalar.AspNetCore's exact
upstream license mapping still targeted 2.16.10 rather than the installed 2.17.4. Exact pin
validation remains enforced, now with a test against the real repository tool manifest.
Scalar's license was fetched from the current package's upstream repository commit and its
SHA-256 verified before updating the versioned provenance assets. Both RID license corpora
now generate successfully; full SPDX validation remains pending its isolated .NET 8 tool runtime.


### Final merge-candidate validation — 2026-09-22

- Final coordinator review identified and corrected Linux routing of `--desktop --no-browser`:
  standalone restarts retain headless behavior, while shell-owned restarts retain their native
  window. A regression test covers both restart paths.
- Final backend gate after that correction: Release zero warnings/errors; main namespace batches
  12,899 passes, persistence 1,216 passes with one visible skip, agent tests 393 passes; zero
  failures and valid assembly guards. Counts are runner-reported batch totals, not unique tests.
- The existing full frontend gate passed (4,770 tests across 471 files, coverage thresholds,
  validation, tooling and production build). Subsequent OpenAPI regeneration passed with no drift;
  final license regeneration produced the same 515-package inventory as the tested frontend.
- Final Python style/type/test/security gate passed. Release-script gate passed, including
  ShellCheck, PSScriptAnalyzer, compile controls and 118 Pester tests with no skips. Documentation
  inventory passed with no missing entries.
- Fresh final Windows and Linux payloads published successfully. Their license corpora and
  reconciled SPDX manifests validated successfully using the required actual .NET 8 runtime:
  Windows 1,156 manifest files, Linux 964, zero validation errors. Each validator reports two
  skipped metadata files; no source/runtime dependency omission was waived.
- The final published Linux shell was also exercised with `--desktop --no-browser`, no X11 or
  Wayland display, and scratch node/runtime roots: its engine returned healthy readiness, SIGTERM
  forwarded cleanly, readiness was removed, and no process-group members survived. This is a
  Linux CLI check under WSL, not Ubuntu native-window acceptance.
- Windows manual lifecycle checks above used the same UI/ownership implementation; the last
  change affects Linux CLI routing only. All Windows acceptance jobs were confirmed cleaned up.
- No production inference thread count or queue limit was raised. Sustained transcription still
  depends on runtime/model throughput; the documented scratch 16-thread comparison is not a new
  production default. Linux tray hiding remains unavailable rather than risking an inaccessible
  window; its restricted GTK capture policy and opt-in compositor workaround remain explicit.
- Real Ubuntu GUI testing is waived. No release was uploaded, no installed-version update was
  applied, and no merge into `develop` was performed. Update coordination is covered by automated
  tests; an actual installed-package update cycle remains release smoke evidence, not claimed here.
