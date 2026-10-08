namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Estimates, before anything is sent, the fixed parts of the next chat request for a model and agent: the resolved
///     system prompt, the offered tools and their template preamble, the output reserve and the safety margin.
/// </summary>
public interface IChatContextEstimateService
{
    /// <summary>
    ///     The pre-send estimate for the request's model and agent, or <see langword="null" /> when the node cannot
    ///     resolve the model.
    /// </summary>
    /// <remarks>
    ///     Read-only: it runs the send path's model head, turn resolution and tool-offer gate, then estimates with the
    ///     outer budgeter's heuristic and the send path's window and reserve rules. Nothing is persisted, staged or sent
    ///     to a model.
    /// </remarks>
    Task<NodeChatContextWindowDto?> EstimateAsync(ChatContextEstimateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the composer would send that shapes the fixed parts of the next request.</summary>
public sealed record ChatContextEstimateRequest
{
    /// <summary>The picked model; blank means the node's local default chat model.</summary>
    public string? ModelName { get; init; }

    /// <summary>The picked agent; null means the seeded Default Assistant.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>The composer's local-tools toggle: off, no tool or preamble is charged.</summary>
    public bool UseLocalTools { get; init; } = true;

    /// <summary>The per-send max output tokens; above the node floor it widens the output reserve. Non-positive is ignored.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>The per-send context window request; capped by a known launched window, else taken as is. Non-positive is ignored.</summary>
    public int? NumCtx { get; init; }
}
