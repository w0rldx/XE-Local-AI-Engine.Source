namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>What the composer would send that shapes the fixed parts of the next request.</summary>
public sealed record ChatContextEstimateRequest
{
    /// <summary>
    ///     The conversation the next request goes to; a plain one takes its own prompt and offers no agent or tools. Null
    ///     means a fresh turn, resolved from the other fields alone.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The picked model; blank means the node's local default chat model.</summary>
    public string? ModelName { get; init; }

    /// <summary>The picked agent; null means the seeded Default Assistant.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>
    ///     The composer's local-tools toggle: off, the node-wide offer is not charged; an agent's own non-empty tool list
    ///     still is, as the send offers it.
    /// </summary>
    public bool UseLocalTools { get; init; } = true;

    /// <summary>The per-send max output tokens; above the node floor it widens the output reserve. Non-positive is ignored.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>The per-send context window request; capped by a known launched window, else taken as is. Non-positive is ignored.</summary>
    public int? NumCtx { get; init; }
}
