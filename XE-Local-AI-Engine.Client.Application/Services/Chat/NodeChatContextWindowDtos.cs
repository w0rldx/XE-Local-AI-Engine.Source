namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>Whether a context-window snapshot describes a provider round that ran or a pre-send estimate.</summary>
/// <remarks>Serialized as the enum name everywhere, including the message metadata blob, so the stored form survives a reorder.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NodeChatContextWindowKind>))]
public enum NodeChatContextWindowKind
{
    /// <summary>The last raw provider round of an assistant turn: provider-reported usage plus per-category estimates.</summary>
    LastRound,

    /// <summary>The fixed parts of the next request (prompt, tools, reserve, margin) estimated before any round ran.</summary>
    PreSendEstimate
}

/// <summary>
///     Content-free account of how one model request occupies its context window: the window and its reserves, what
///     the provider reported, and a heuristic split of the input by category. Names and counts only.
/// </summary>
/// <remarks>
///     Never prompt text, tool descriptions, schemas, arguments or results, so it is safe to persist, stream and return.
///     Captured on the last raw provider round by the provider-boundary budget hop, the only place that sees the
///     message set and tool list actually sent (after relevance narrowing). "Remaining" is derived by the client as
///     <see cref="UsableWindowTokens" /> minus <see cref="ProviderInputTokens" /> (or the estimated total pre-send).
/// </remarks>
public sealed record NodeChatContextWindowDto
{
    public required NodeChatContextWindowKind Kind { get; init; }

    /// <summary>The served model the numbers belong to; <see langword="null" /> when unknown.</summary>
    public string? ModelId { get; init; }

    /// <summary>The window the round was measured against (the launched window when known, else the configured default).</summary>
    public required int WindowTokens { get; init; }

    /// <summary>Output tokens held back from the window before the input was measured.</summary>
    public required int ReservedOutputTokens { get; init; }

    /// <summary>The input budget after the estimate safety factor, the model's observed correction and the output reserve.</summary>
    public required int UsableWindowTokens { get; init; }

    /// <summary>What the two estimate margins withheld: <c>WindowTokens - ReservedOutputTokens - UsableWindowTokens</c>.</summary>
    public required int SafetyMarginTokens { get; init; }

    /// <summary>Prompt tokens the provider counted for the round; <see langword="null" /> pre-send or when the provider reported none.</summary>
    public int? ProviderInputTokens { get; init; }

    public int? ProviderOutputTokens { get; init; }

    public int? ProviderReasoningTokens { get; init; }

    /// <summary>Heuristic per-category split of the input; <see langword="null" /> when no estimate was taken.</summary>
    public NodeChatContextWindowEstimate? Estimated { get; init; }

    /// <summary>The tools actually sent in the round (or offered, pre-send), names and estimated tokens only; capped at <see cref="MaxToolEntries" />.</summary>
    public IReadOnlyList<NodeChatContextWindowTool> Tools { get; init; } = [];

    /// <summary>Tools the relevance filter withheld from the round (still reachable through <c>list_tools</c>); zero pre-send.</summary>
    public int ToolsWithheldCount { get; init; }

    /// <summary>What the per-round budgeter cut to fit; <see langword="null" /> pre-send.</summary>
    public NodeChatContextWindowTrim? Trimmed { get; init; }

    /// <summary>Upper bound on <see cref="Tools" /> so a tool-heavy agent cannot grow the metadata blob without limit.</summary>
    public const int MaxToolEntries = ProviderRoundContextSnapshot.MaxToolEntries;
}

/// <summary>Heuristic token split of one request's input. Every value is an estimate from the calibrated character heuristic.</summary>
public sealed record NodeChatContextWindowEstimate
{
    /// <summary>The resolved system prompt (scaffold, persona and playbook memory as one unit).</summary>
    public required int SystemPromptTokens { get; init; }

    /// <summary>Instructions added at the agent boundary beyond the system prompt, in practice the skill listing.</summary>
    public required int InstructionsTokens { get; init; }

    /// <summary>Every sent tool's name, description and schema, excluding the template preamble.</summary>
    public required int ToolSchemaTokens { get; init; }

    /// <summary>The chat template's once-per-request tool instructions, measured per model; zero when no tool was sent.</summary>
    public required int ToolTemplatePreambleTokens { get; init; }

    /// <summary>Knowledge-base excerpts injected as leading context.</summary>
    public required int KnowledgeTokens { get; init; }

    /// <summary>Inlined attachment text and image parts injected as leading context.</summary>
    public required int AttachmentTokens { get; init; }

    /// <summary>The compaction state and synopsis message.</summary>
    public required int CompactionTokens { get; init; }

    /// <summary>Everything else: user and assistant turns, tool calls and tool results, including loaded skill bodies.</summary>
    public required int ConversationTokens { get; init; }

    /// <summary>Sum of the categories above, the estimated input of the round as sent.</summary>
    public required int TotalTokens { get; init; }
}

/// <summary>One sent tool: its model-facing name and the estimated tokens of its definition.</summary>
public sealed record NodeChatContextWindowTool
{
    public required string Name { get; init; }

    public required int Tokens { get; init; }
}

/// <summary>Counters of what the per-round budgeter removed to fit the window.</summary>
public sealed record NodeChatContextWindowTrim
{
    public required int MessagesDropped { get; init; }

    public required int ToolResultsTruncated { get; init; }

    public required int ReasoningStripped { get; init; }
}
