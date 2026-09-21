namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One transcript row of a <see cref="TranscriptionSession" />. Append-only: <see cref="Seq" /> is allocated by
///     the caller and a unique <c>(session_id, seq)</c> index stops a double allocation.
/// </summary>
/// <remarks>
///     <see cref="Text" /> is stored encrypted at rest (AAD column name <c>transcript_segment_text</c>, binding the
///     session id and the segment id), so a row moved to another session fails its tag check instead of surfacing as
///     that session's transcript.
/// </remarks>
internal sealed record class TranscriptSegment
{
    /// <summary>Segment identity (PK).</summary>
    public Guid Id { get; set; }

    /// <summary>The owning session.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Position within the session, ascending. Unique per session.</summary>
    public long Seq { get; set; }

    /// <summary>Segment start offset from the beginning of the audio, in milliseconds.</summary>
    public long StartMs { get; set; }

    /// <summary>Segment end offset from the beginning of the audio, in milliseconds.</summary>
    public long EndMs { get; set; }

    /// <summary>
    ///     UTF-8 transcript text bytes. Plaintext while tracked in memory; encrypted at rest using AAD column name
    ///     <c>transcript_segment_text</c>.
    /// </summary>
    public byte[] Text { get; set; } = [];

    /// <summary>Which capture channel produced this segment.</summary>
    public TranscriptChannel Channel { get; set; }

    /// <summary>The runtime's confidence for this segment, when it reports one.</summary>
    public double? Confidence { get; set; }
}
