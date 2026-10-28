namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Local audio transcription: the whisper.cpp runtime, its managed source build, and the model catalog.
    /// </summary>
    /// <remarks>
    ///     The whole surface is gated on <c>Transcription:Enabled</c> by request-path middleware in <c>Program</c> that
    ///     answers 404 for anything under <see cref="Root" />. The session routes sit under that same root, so the gate
    ///     covers them too.
    /// </remarks>
    public static class Transcription
    {
        /// <summary>The prefix the feature gate matches; every route below sits under it.</summary>
        public const string Root = "transcription";

        // Managed whisper.cpp runtime and its Linux source-build orchestration.
        public const string Runtime = "transcription/runtime";
        public const string RuntimeEject = "transcription/runtime/eject";
        public const string RuntimeRecommendation = "transcription/runtime/recommendation";
        public const string RuntimeSourceBuild = "transcription/runtime/source-build";
        public const string RuntimeSourceBuildPrerequisites = "transcription/runtime/source-build/prerequisites";
        public const string RuntimeSourceBuildStatus = "transcription/runtime/source-build/status";
        public const string RuntimeSourceBuildCancel = "transcription/runtime/source-build/cancel";
        public const string RuntimeSourceBuildRemove = "transcription/runtime/source-build/remove";

        // Whisper weight catalog: what exists, what is installed, what is downloading, and which one is selected.
        public const string Models = "transcription/models";
        public const string ModelDownloads = "transcription/models/downloads";
        public const string ModelDownloadCancel = "transcription/models/downloads/cancel";
        public const string ModelSelect = "transcription/models/select";

        // Transcription sessions: the transcript rows an operator keeps, their lifecycle, and the batch file upload.
        public const string Sessions = "transcription/sessions";
        public const string SessionById = "transcription/sessions/{sessionId}";
        public const string SessionCancel = "transcription/sessions/{sessionId}/cancel";
        public const string SessionFile = "transcription/sessions/{sessionId}/file";

        /// <summary>Replaces one transcript row's text (PUT). Refused with 409 while the session is still transcribing.</summary>
        public const string SessionSegmentBySeq = "transcription/sessions/{sessionId}/segments/{seq}";

        /// <summary>
        ///     Starts live capture for an existing session. Idempotent and body-less; the client awaits it before it
        ///     forwards a single audio frame.
        /// </summary>
        public const string SessionLiveStart = "transcription/sessions/{sessionId}/live/start";

        /// <summary>
        ///     Starts an ephemeral dictation session: live lanes in memory, nothing persisted. A separate verb rather
        ///     than a flag on session create, so no caller can open a persisted-looking session that never lists.
        /// </summary>
        public const string DictationSessions = "transcription/dictation-sessions";

        /// <summary>
        ///     Windows-only: the processes that currently hold an active render audio session, i.e. the ones
        ///     per-application capture can actually target. Empty — never 404 — on a host without process loopback.
        /// </summary>
        public const string CaptureProcesses = "transcription/capture/processes";

        /// <summary>
        ///     Starts (POST) and stops (DELETE) server-side per-application capture for a session that is ALREADY
        ///     live, so <see cref="SessionLiveStart" /> must have run first. There is no scope parameter: WASAPI
        ///     offers only "the target process and its descendants".
        /// </summary>
        public const string SessionProcessCapture = "transcription/sessions/{sessionId}/capture/process";

        // SignalR push hub for live transcription sessions: a full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the other local hubs. It shares the
        // family's first segment, so the feature gate's 404 covers its negotiate too.
        public const string Hub = "/api/local/v1/transcription/hub";
    }
}
