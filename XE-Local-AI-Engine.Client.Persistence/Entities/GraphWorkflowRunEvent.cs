namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One append-only entry in a run's change log. <see cref="Seq" /> is allocated from the run's watermark inside the
///     transaction that writes it, so a subscriber replaying from a sequence sees every row that followed it.
/// </summary>
internal sealed class GraphWorkflowRunEvent
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public long Seq { get; set; }
    public string EventType { get; set; } = string.Empty;

    /// <summary>The node the event belongs to, when it belongs to one. Structural, so plaintext.</summary>
    public string? NodeKey { get; set; }

    /// <summary>Small structured payloads only — a failure summary, a decision outcome. Never a transcript.</summary>
    public byte[]? DetailJson { get; set; }

    public long CreatedAtUtc { get; set; }
}
