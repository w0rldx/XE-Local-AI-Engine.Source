namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>How the language of a transcription request is decided.</summary>
public enum WhisperLanguageMode
{
    /// <summary>Let the model detect the language.</summary>
    Auto = 0,

    /// <summary>Use <see cref="WhisperTranscriptionRequest.LanguageCode" /> verbatim.</summary>
    Explicit = 1
}

/// <summary>
///     One transcription request, expressed entirely in engine terms. No whisper-server flag, route, multipart field
///     or JSON key appears here: this record and <see cref="WhisperTranscriptionResult" /> ARE the boundary that keeps
///     the daemon's wire protocol inside this project.
/// </summary>
public sealed record WhisperTranscriptionRequest
{
    /// <summary>
    ///     Caller-owned, <b>seekable</b> audio stream. The provider neither buffers it to disk nor disposes it.
    /// </summary>
    /// <remarks>
    ///     Seekable is a hard requirement, not a preference: only a seekable stream lets the multipart body report a
    ///     content length, and a length-less body would be sent chunked, which the daemon's multipart parser is not
    ///     verified against. The adapter rejects a non-seekable stream rather than discovering this at the far end.
    /// </remarks>
    public required Stream Audio { get; init; }

    /// <summary>The audio's media type, for example <c>audio/wav</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>Whether the language is detected or supplied.</summary>
    public WhisperLanguageMode LanguageMode { get; init; } = WhisperLanguageMode.Auto;

    /// <summary>The language code to force; required when <see cref="LanguageMode" /> is explicit.</summary>
    public string? LanguageCode { get; init; }

    /// <summary>Translate the result to English. Translation is to English only — there is no other target.</summary>
    public bool Translate { get; init; }

    /// <summary>Run voice-activity detection, so silence is not submitted to the model.</summary>
    public bool UseVoiceActivityDetection { get; init; } = true;

    /// <summary>
    ///     Ask for the detected language. Computing the language probabilities is expensive and is skipped unless this
    ///     is set, which is why the daemon is launched with them off by default.
    /// </summary>
    public bool DetectLanguage { get; init; } = true;
}

/// <summary>One transcribed segment. Times are seconds from the start of the submitted audio.</summary>
/// <param name="StartSeconds">Segment start, in seconds.</param>
/// <param name="EndSeconds">Segment end, in seconds.</param>
/// <param name="Text">The segment's text, trimmed.</param>
/// <param name="Confidence">
///     A 0..1 confidence derived from the model's average log probability, or <see langword="null" /> when the model
///     reported none. The daemon has no confidence field of its own; this is a derived value, not a reported one.
/// </param>
public sealed record WhisperTranscriptSegment(double StartSeconds, double EndSeconds, string Text, double? Confidence);

/// <summary>The result of one transcription.</summary>
/// <param name="Text">The full transcript.</param>
/// <param name="Segments">The timed segments, in order.</param>
/// <param name="DetectedLanguageCode">The detected ISO language code, or <see langword="null" /> when not requested.</param>
/// <param name="DetectedLanguageProbability">The detector's confidence, when one was reported.</param>
/// <param name="DurationSeconds">The duration of the audio the model processed.</param>
public sealed record WhisperTranscriptionResult(
    string Text,
    IReadOnlyList<WhisperTranscriptSegment> Segments,
    string? DetectedLanguageCode,
    double? DetectedLanguageProbability,
    double DurationSeconds);

/// <summary>
///     The only thing the engine ever calls to transcribe audio. Everything about how the daemon is reached — the
///     route, the multipart field names, the response shape — lives behind this one method.
/// </summary>
public interface IWhisperTranscriber
{
    /// <summary>
    ///     Transcribes <paramref name="request" />'s audio with <paramref name="modelId" />, starting or reusing the
    ///     runtime as needed and holding a transcription lease for the duration of the call.
    /// </summary>
    /// <exception cref="WhisperRuntimeException">
    ///     The runtime could not be started, is busy with an exclusive operation, changed models while the request was
    ///     starting, or rejected the audio. The message is sanitized for display.
    /// </exception>
    /// <exception cref="ArgumentException">The audio stream is not seekable.</exception>
    Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct);
}
