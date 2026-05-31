namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying invocation completed payload data.
/// </summary>
public sealed record InvocationCompletedPayload
{
    public required Guid InvocationId { get; init; }

    public required string FinalContent { get; init; }

    public string? ModelUsed { get; init; }

    /// <summary>Prompt/input tokens reported by the model backend on the terminal response chunk.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Completion/output tokens reported by the model backend on the terminal response chunk.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Total tokens reported by the model backend on the terminal response chunk.</summary>
    public int? TokensUsed { get; init; }

    public string? FinalReasoning { get; init; }

    public int? ReasoningTokens { get; init; }
}
