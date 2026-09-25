namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using System.Text.Json.Serialization;

/// <summary>
///     The fixed kinds of fact a conversation's distilled state keeps. Fixed so the injected block groups
///     deterministically and the reducer's budget policy can rank what to drop first.
/// </summary>
public enum ConversationStateCategory
{
    Goal,
    Decision,
    Constraint,
    Fact,
    Correction,
    OpenQuestion,
    ToolOutcome,
    CompletedWork
}

/// <summary>
///     One distilled statement with provenance. An entry is never edited in place: a later statement that replaces
///     it sets <see cref="SupersededById" />, so a correction keeps both the old and the new value visible.
/// </summary>
public sealed class ConversationStateEntry
{
    /// <summary>Short stable id the reducer assigns (e.g. <c>e17</c>); the distiller refers to entries by it.</summary>
    public required string Id { get; init; }

    public required ConversationStateCategory Category { get; init; }

    public required string Value { get; init; }

    /// <summary>Anchor sequences of the messages this entry was distilled from.</summary>
    public required IReadOnlyList<int> SourceSequences { get; init; }

    /// <summary>The id of the entry that replaced this one, or null while it is live.</summary>
    public string? SupersededById { get; init; }

    /// <summary>Anchor sequence of the message that retired this entry without a replacement, or null while it is live.</summary>
    public int? RetiredAtSequence { get; init; }

    /// <summary>Highest anchor sequence the distiller had seen when it minted the entry.</summary>
    public required int CreatedAtSequence { get; init; }

    [JsonIgnore]
    public bool IsLive => SupersededById is null && RetiredAtSequence is null;
}

/// <summary>The persisted state document: versioned so a later shape change can migrate on read.</summary>
public sealed class ConversationStateDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The number the next minted id takes; persisted so an id is never reused after the budget drops its entry.</summary>
    public int NextEntryNumber { get; init; } = 1;

    public IReadOnlyList<ConversationStateEntry> Entries { get; init; } = [];
}

/// <summary>A new entry the distiller proposes; the reducer assigns its id.</summary>
public sealed class ConversationStateProposedEntry
{
    public required ConversationStateCategory Category { get; init; }

    public required string Value { get; init; }

    public IReadOnlyList<int> SourceSequences { get; init; } = [];

    /// <summary>Ids of existing entries this one replaces (a correction or an updated decision).</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];
}

/// <summary>Retires an existing entry without a replacement (a constraint lifted, a goal abandoned).</summary>
public sealed class ConversationStateRetirement
{
    public required string EntryId { get; init; }

    /// <summary>Anchor sequence of the message that retired it.</summary>
    public required int BySequence { get; init; }
}

/// <summary>
///     What one distillation pass changes. The model never rewrites the whole document; it only proposes additions,
///     retirements and resolved open questions, which is what stops summary-of-summary drift.
/// </summary>
public sealed class ConversationStateDelta
{
    public IReadOnlyList<ConversationStateProposedEntry> Add { get; init; } = [];

    public IReadOnlyList<ConversationStateRetirement> Supersede { get; init; } = [];

    /// <summary>Ids of <see cref="ConversationStateCategory.OpenQuestion" /> entries the new span answered.</summary>
    public IReadOnlyList<string> Resolve { get; init; } = [];

    public bool IsEmpty => Add.Count == 0 && Supersede.Count == 0 && Resolve.Count == 0;
}

/// <summary>A compact rendering of one tool call inside an assistant message, so tool OUTCOMES survive the turn.</summary>
public sealed class ConversationStateToolPart
{
    public required string Name { get; init; }

    public string? ArgumentsExcerpt { get; init; }

    public string? ResultExcerpt { get; init; }
}

/// <summary>One completed message handed to the distiller: anchor sequence, role, decrypted content, tool parts.</summary>
public sealed class ConversationStateSourceMessage
{
    /// <summary>Anchor sequence (the same space the compaction watermarks use).</summary>
    public required int Sequence { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    public IReadOnlyList<ConversationStateToolPart> Tools { get; init; } = [];
}

/// <summary>Input for one distillation pass over the messages since the state watermark.</summary>
public sealed class ConversationStateDistillerInput
{
    public required ConversationStateDocument State { get; init; }

    public required IReadOnlyList<ConversationStateSourceMessage> Messages { get; init; }

    /// <summary>An installed LOCAL chat model; the distiller never resolves a cloud client.</summary>
    public required string ModelName { get; init; }

    public bool SupportsThinking { get; init; }

    /// <summary>The model's resident context window in tokens; null keeps the configured per-call ceiling.</summary>
    public int? EffectiveContextTokens { get; init; }
}

/// <summary>The result of one distiller call: the delta for the leading messages that fit the call's budget.</summary>
public sealed class ConversationStateDistillation
{
    public required ConversationStateDelta Delta { get; init; }

    /// <summary>How many leading messages of the input this call consumed; always at least 1.</summary>
    public required int MessagesConsumed { get; init; }

    /// <summary>Anchor sequence of the last consumed message: the watermark the caller persists with the reduced state.</summary>
    public required int CoversToSequence { get; init; }
}

/// <summary>
///     Distils completed messages into a <see cref="ConversationStateDelta" /> on a NODE-LOCAL model only, the same
///     privacy invariant as <c>IConversationSummarizer</c>.
/// </summary>
/// <remarks>
///     One call consumes as many LEADING messages as fit its calibrated budget (an oversized message is excerpted so
///     progress is always at least one). The caller applies the delta, persists the watermark, drops the consumed
///     messages and calls again with the reduced state, so a failed call never advances coverage over unprocessed span.
/// </remarks>
public interface IConversationStateDistiller
{
    /// <summary>
    ///     Returns the delta the model proposed for the consumed messages (possibly empty), or null when the model
    ///     produced no parseable delta; the caller then leaves the state and its watermark untouched.
    /// </summary>
    Task<ConversationStateDistillation?> DistillAsync(ConversationStateDistillerInput input, CancellationToken cancellationToken = default);
}
