namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

/// <summary>The SignalR method names a live transcription client listens on.</summary>
public static class TranscriptionHubEvents
{
    public const string SegmentCommitted = "transcriptionSegmentCommitted";

    public const string PartialUpdated = "transcriptionPartialUpdated";

    public const string SessionStatusChanged = "transcriptionSessionStatusChanged";

    public const string CatchUpProgress = "transcriptionCatchUpProgress";

    public const string AdmissionClosed = "transcriptionAdmissionClosed";

    public const string SourceQuiet = "transcriptionSourceQuiet";
}

/// <summary>
///     The refusal codes this hub throws as <see cref="HubException" /> messages.
/// </summary>
/// <remarks>
///     Stable strings the client matches on: a live capture UI has to tell "your session ended" apart from "your frame
///     was malformed", and an exception message written inline would change the contract the next time somebody
///     reworded it.
/// </remarks>
public static class TranscriptionHubErrors
{
    public const string Disabled = "transcription-disabled";

    public const string SessionRequired = "transcription-session-required";

    public const string SessionNotFound = "transcription-session-not-found";

    public const string NotTranscribing = "transcription-session-not-transcribing";

    public const string FrameTooLarge = "transcription-frame-too-large";

    public const string FrameMisaligned = "transcription-frame-misaligned";

    public const string UnknownChannel = "transcription-unknown-channel";

    public const string InvalidWatermark = "transcription-invalid-watermark";
}

/// <summary>
///     What a subscriber gets back: the session's status, the transcript rows after its watermark, the watermark it
///     may resume from, and whether the replay cap cut the page short.
/// </summary>
public sealed class TranscriptionSessionSubscriptionSnapshot
{
    /// <summary>The session subscribed to.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The session's persisted status, or <c>Transcribing</c> for a persist-free session.</summary>
    public required string Status { get; init; }

    /// <summary>The last row DELIVERED, or the caller's own watermark when nothing was.</summary>
    public required long LastSeq { get; init; }

    /// <summary>The replayed rows, ascending by sequence.</summary>
    public required IReadOnlyList<TranscriptSegmentResponse> Segments { get; init; }

    /// <summary>Whether rows beyond this page exist; read one row past the cap, never inferred.</summary>
    public required bool ReplayTruncated { get; init; }
}

/// <summary>One committed transcript row, pushed as it is allocated its sequence.</summary>
public sealed class TranscriptSegmentCommittedPush
{
    public required Guid SessionId { get; init; }

    public required long Seq { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required string Text { get; init; }

    public required string Channel { get; init; }

    public required double? Confidence { get; init; }
}

/// <summary>One lane's provisional text. Never persisted and never sequenced: it is replaced, not accumulated.</summary>
public sealed class TranscriptPartialUpdatedPush
{
    public required Guid SessionId { get; init; }

    public required string Channel { get; init; }

    public required string Text { get; init; }
}

/// <summary>A live session reached its terminal state, and this is what it was.</summary>
public sealed class TranscriptionSessionStatusPush
{
    public required Guid SessionId { get; init; }

    public required string Status { get; init; }

    /// <summary><c>live-never-attached</c> or <c>live-failed</c> when the status alone does not say why; otherwise null.</summary>
    public string? ErrorCode { get; init; }
}
