namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>What a queued background conversation-maintenance job does.</summary>
public enum ConversationMaintenanceKind
{
    /// <summary>Fold older turns into the synopsis when the next turn's projected history crosses the auto-compact threshold.</summary>
    Compact,

    /// <summary>
    ///     Distil the completed messages after the state watermark into the structured state once enough of them
    ///     (<c>DistillEveryMessages</c>) or enough estimated tokens (<c>DistillEveryTokens</c>) have accumulated.
    /// </summary>
    Distill
}

/// <summary>One queued conversation-maintenance job; content-free, the worker reloads the conversation itself.</summary>
public sealed class ConversationMaintenanceJob
{
    public required Guid ConversationId { get; init; }

    public required ConversationMaintenanceKind Kind { get; init; }

    /// <summary>The model the turn ran on, for its calibrated token divisor and observed correction; null falls back to the default estimate.</summary>
    public string? ModelName { get; init; }

    /// <summary>The turn's context window after the launched window was folded in (<c>TurnPolicy.ContextCapacityTokens</c>).</summary>
    public required int ContextCapacityTokens { get; init; }

    /// <summary>The output tokens that window held back for the answer (<c>TurnPolicy.ReservedOutputTokens</c>).</summary>
    public required int ReservedOutputTokens { get; init; }
}
