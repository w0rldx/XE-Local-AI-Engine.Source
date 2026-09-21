namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A persisted transcription session (create → transcribe → complete/fail/cancel). Node-scoped. The audio itself
///     is never persisted — only the derived transcript rows in <see cref="Segments" />.
/// </summary>
/// <remarks>
///     <see cref="Title" />, <see cref="ConfigJson" />, <see cref="ErrorCode" /> and <see cref="ErrorMessage" /> are
///     stored encrypted at rest (AES-256-GCM, node key) — see <c>NodeEncryptionSaveChangesInterceptor</c> /
///     <c>NodeEncryptionMaterializationInterceptor</c> (AAD column names <c>transcription_session_*</c>).
/// </remarks>
internal sealed record class TranscriptionSession
{
    /// <summary>Session identity (PK).</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     UTF-8 display title bytes, or <see langword="null" />. Plaintext while tracked in memory; encrypted at rest
    ///     using AAD column name <c>transcription_session_title</c>.
    /// </summary>
    public byte[]? Title { get; set; }

    /// <summary>When the session was created (unix ms UTC).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>When the session last changed state (unix ms UTC).</summary>
    public long UpdatedAtUtc { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public TranscriptionSessionStatus Status { get; set; }

    /// <summary>Where the audio for this session comes from.</summary>
    public TranscriptionSourceKind SourceKind { get; set; }

    /// <summary>Canonical whisper model id the session transcribes with.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    ///     UTF-8 JSON bytes of the session's resolved transcription configuration (language mode, translate flag, …).
    ///     Plaintext while tracked; encrypted at rest using AAD column name <c>transcription_session_config_json</c>.
    /// </summary>
    /// <remarks>
    ///     Required — a session with no options stores <c>{}</c>, never null.
    /// </remarks>
    public byte[] ConfigJson { get; set; } = [];

    /// <summary>The language the runtime detected, as a BCP-47-ish short code, or <see langword="null" />. Structural, so plaintext.</summary>
    public string? DetectedLanguage { get; set; }

    /// <summary>Audio duration in milliseconds once transcription completes, or <see langword="null" />.</summary>
    public long? DurationMs { get; set; }

    /// <summary>
    ///     UTF-8 machine-readable failure code, or <see langword="null" />. Encrypted at rest with the message as a pair
    ///     (AAD column name <c>transcription_session_error_code</c>), so it is deliberately not SQL-queryable.
    /// </summary>
    public byte[]? ErrorCode { get; set; }

    /// <summary>
    ///     UTF-8 display-safe failure message, or <see langword="null" />. Encrypted at rest using AAD column name
    ///     <c>transcription_session_error_message</c>.
    /// </summary>
    public byte[]? ErrorMessage { get; set; }

    /// <summary>The session's transcript rows. Cascade-deleted with the session.</summary>
    public List<TranscriptSegment> Segments { get; } = [];
}
