namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Content-free account of one raw provider round as the budget hop sent it: the window and its reserves, a
///     heuristic token split of the input by category, the tools sent and what the budgeter trimmed.
/// </summary>
/// <remarks>
///     Names and counts only, never message text, tool descriptions, schemas, arguments or results. Every token value
///     is the calibrated character heuristic the budgeter itself used, so the categories sum to
///     <see cref="EstimatedInputTokens" />. Captured last-wins on the ambient <see cref="ProviderCallBudget" />.
/// </remarks>
public sealed record ProviderRoundContextSnapshot
{
    /// <summary>Upper bound on <see cref="Tools" />, so a tool-heavy agent cannot grow a persisted snapshot without limit.</summary>
    public const int MaxToolEntries = 64;

    public string? ModelId { get; init; }

    /// <summary>The window the round was measured against.</summary>
    public required int WindowTokens { get; init; }

    public required int ReservedOutputTokens { get; init; }

    /// <summary>The input budget after both estimate margins and the output reserve.</summary>
    public required int UsableWindowTokens { get; init; }

    /// <summary>Every <c>System</c> message sent: the resolved system prompt.</summary>
    public required int SystemPromptTokens { get; init; }

    /// <summary><c>ChatOptions.Instructions</c>, in production only what the agent boundary adds (the skill listing).</summary>
    public required int InstructionsTokens { get; init; }

    /// <summary>The sent tools' definitions, without the template preamble.</summary>
    public required int ToolSchemaTokens { get; init; }

    public required int ToolTemplatePreambleTokens { get; init; }

    public required int KnowledgeTokens { get; init; }

    /// <summary>Inlined attachment text and image parts together.</summary>
    public required int AttachmentTokens { get; init; }

    public required int CompactionTokens { get; init; }

    public required int ConversationTokens { get; init; }

    /// <summary>The budgeter's estimate of the round's whole input after trimming; the sum of the categories above.</summary>
    public required int EstimatedInputTokens { get; init; }

    /// <summary>The sent tools in offer order, capped at <see cref="MaxToolEntries" />.</summary>
    public required IReadOnlyList<ProviderRoundToolTokens> Tools { get; init; }

    /// <summary>Tools the relevance filter withheld from this round.</summary>
    public required int ToolsWithheldCount { get; init; }

    public required int MessagesDropped { get; init; }

    public required int ToolResultsTruncated { get; init; }

    public required int ReasoningStripped { get; init; }

    /// <summary>Prompt tokens the provider reported for THIS round; null until it completes, or when it reported none.</summary>
    public int? ProviderInputTokens { get; init; }

    public int? ProviderOutputTokens { get; init; }

    /// <summary>Reasoning tokens the provider reported for this round, counted inside <see cref="ProviderOutputTokens" />.</summary>
    public int? ProviderReasoningTokens { get; init; }
}
