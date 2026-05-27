namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
/// Per-message feedback (thumbs/rating + optional free-text comment). Stored node-local only — feedback
/// never syncs back to the platform, mirroring the Origin=Remote view-only posture. One row per message:
/// the message id is the primary key, so re-submitting feedback overwrites the prior row.
/// </summary>
internal sealed record class NodeMessageFeedback
{
    public Guid MessageId { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>
    /// Coarse sentiment: <see cref="NodeMessageFeedbackRating.Up"/> / <see cref="NodeMessageFeedbackRating.Down"/>.
    /// </summary>
    public string Rating { get; set; } = string.Empty;

    /// <summary>
    /// Optional free-text comment. Plaintext at rest (documented posture, single-user device).
    /// </summary>
    public string? Comment { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    public NodeMessage? Message { get; set; }
}

internal static class NodeMessageFeedbackRating
{
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Up,
        Down
    };
}
