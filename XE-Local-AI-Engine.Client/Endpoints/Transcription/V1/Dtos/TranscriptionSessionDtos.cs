namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     A transcription session without its transcript — what the session list renders. Timestamps are unix-ms and
///     <see cref="Status" /> / <see cref="SourceKind" /> are the enum names. No audio path is ever surfaced: the
///     uploaded bytes live only for the length of one transcription and are deleted with the upload slot.
/// </summary>
public sealed class TranscriptionSessionSummaryResponse
{
    public required Guid Id { get; init; }

    public string? Title { get; init; }

    /// <summary><c>Created</c> / <c>Transcribing</c> / <c>Completed</c> / <c>Failed</c> / <c>Cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary><c>File</c> / <c>Microphone</c> / <c>SystemAudio</c> / <c>MicrophoneAndSystem</c> / <c>Dictation</c> / <c>ApplicationProcess</c>.</summary>
    public required string SourceKind { get; init; }

    public required string ModelId { get; init; }

    public string? DetectedLanguage { get; init; }

    public long? DurationMs { get; init; }

    /// <summary>How many transcript rows the session holds; counted in SQL, so the list stays cheap.</summary>
    public required int SegmentCount { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>One transcript row. <see cref="Text" /> is the decrypted plaintext, returned only to the operator.</summary>
public sealed class TranscriptSegmentResponse
{
    public required Guid Id { get; init; }

    /// <summary>Ascending from 1 within a session; 0 is never used, so "no segments yet" cannot look like segment zero.</summary>
    public required long Seq { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required string Text { get; init; }

    /// <summary><c>Mono</c> / <c>You</c> / <c>Others</c>.</summary>
    public required string Channel { get; init; }

    public double? Confidence { get; init; }
}

/// <summary>The options one session runs under, read back from the session's encrypted config column.</summary>
public sealed class TranscriptionSessionConfigResponse
{
    /// <summary><c>auto</c> or <c>override</c>.</summary>
    public required string LanguageMode { get; init; }

    public string? LanguageOverride { get; init; }

    public required bool Translate { get; init; }

    public required int MaxWindowSeconds { get; init; }

    public required bool ChannelAttribution { get; init; }
}

/// <summary>
///     One session with its transcript and its options. <see cref="ErrorCode" /> / <see cref="ErrorMessage" /> carry
///     the sanitized failure reason when the session failed, and are null otherwise.
/// </summary>
public sealed class TranscriptionSessionDetailResponse
{
    public required TranscriptionSessionSummaryResponse Session { get; init; }

    /// <summary>Ordered by <see cref="TranscriptSegmentResponse.Seq" />; the client renders that order, never re-sorts.</summary>
    public required IReadOnlyList<TranscriptSegmentResponse> Segments { get; init; }

    public required TranscriptionSessionConfigResponse Config { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>Response envelope for <c>GET transcription/sessions</c>, newest first, with the unpaged total.</summary>
public sealed class ListTranscriptionSessionsResponse
{
    public required IReadOnlyList<TranscriptionSessionSummaryResponse> Items { get; init; }

    /// <summary>How many sessions exist in total, ignoring paging — what lets the client show a real pager.</summary>
    public required int TotalCount { get; init; }
}

/// <summary>Query for <c>GET transcription/sessions</c>; both bounds are clamped in the handler.</summary>
public sealed class ListTranscriptionSessionsRequest
{
    public int? Limit { get; init; }

    public int? Offset { get; init; }
}

/// <summary>Route-only request for the session id shared by get, delete and cancel.</summary>
public sealed class TranscriptionSessionRouteRequest
{
    public Guid SessionId { get; init; }
}

/// <summary>
///     The options a new session is created with. The title is decided here and never at upload time, so the file
///     flow sends the chosen file's name; a blank one falls back to a generated <c>Transcription {date}</c>.
/// </summary>
public sealed class CreateTranscriptionSessionRequest
{
    public string? Title { get; init; }

    /// <summary><c>File</c> in this release; the capture kinds arrive with the live slices.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Null or blank resolves to the node's selected model, and failing that to its recommendation.</summary>
    public string? ModelId { get; init; }

    /// <summary><c>auto</c> or <c>override</c>.</summary>
    public string LanguageMode { get; init; } = "auto";

    /// <summary>The ISO-639-1 code to force; required when <see cref="LanguageMode" /> is <c>override</c>.</summary>
    public string? LanguageOverride { get; init; }

    public bool Translate { get; init; }

    public int MaxWindowSeconds { get; init; } = 5;

    public bool ChannelAttribution { get; init; }
}

/// <summary>The multipart request for <c>POST transcription/sessions/{sessionId}/file</c>.</summary>
public sealed class UploadTranscriptionAudioRequest
{
    /// <summary>Bound from the route. Disabling form auto-binding does not disable route binding.</summary>
    public Guid SessionId { get; init; }

    /// <summary>
    ///     OpenAPI metadata ONLY. Form auto-binding is disabled on this endpoint so the audio never reaches a
    ///     framework-owned temp file, which means this property is always null at runtime and must never be read.
    ///     The bytes are streamed from <c>FormFileSectionsAsync()</c> straight into the engine-owned upload slot.
    ///     <para>
    ///         Declared non-nullable and <see cref="RequiredAttribute" /> because the endpoint refuses a request
    ///         carrying no file with a 400: a schema that permits an absent or null file describes an endpoint that
    ///         does not exist, and the generated client types the body off that schema. The <c>null!</c> is the price
    ///         of saying so without re-enabling the binding that would buffer the audio — nothing ever assigns or
    ///         reads this member.
    ///     </para>
    /// </summary>
    [Required]
    public IFormFile File { get; init; } = null!;
}

/// <summary>
///     The typed 415 body for audio this node cannot decode. A returned value rather than a thrown exception — no
///     <c>IExceptionHandler</c> sits on that path — so it follows ADR 0009's per-feature block shape.
/// </summary>
public sealed class TranscriptionUnsupportedContainerResponse
{
    /// <summary><c>ffmpeg-required</c> when the container is real but ffmpeg is absent, else <c>unsupported-container</c>.</summary>
    public required string Reason { get; init; }

    public required string Message { get; init; }

    /// <summary>The <c>AudioContainer</c> name the bytes actually sniffed as, <c>Unknown</c> included.</summary>
    public required string DetectedContainer { get; init; }

    /// <summary>The extensions this node accepts right now — it grows when ffmpeg is installed.</summary>
    public required IReadOnlyList<string> SupportedContainers { get; init; }

    /// <summary>True when installing ffmpeg would make this exact upload work. What turns a refusal into an action.</summary>
    public required bool FfmpegRequired { get; init; }
}
