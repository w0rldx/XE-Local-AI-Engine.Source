namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One task item as it is written. The store owns <c>Index</c>, <c>Revision</c> and <c>InputHash</c> — a caller
///     that could name them could present a stale answer as a current one.
/// </summary>
public sealed record BenchmarkTaskItemInput
{
    public required ReadOnlyMemory<byte> PromptJson { get; init; }

    public string Kind { get; init; } = BenchmarkTaskItemKinds.Prompt;

    public ReadOnlyMemory<byte>? ReferenceAnswerJson { get; init; }

    public ReadOnlyMemory<byte>? VerifierConfigJson { get; init; }

    public ReadOnlyMemory<byte>? GeneratorConfigJson { get; init; }

    /// <summary>Empty mints a new one. Named explicitly only so a generated case can be created deterministically.</summary>
    public Guid Id { get; init; }

    /// <summary>The generator this item was expanded from, or null for an authored item.</summary>
    public Guid? ParentItemId { get; init; }

    /// <summary>Whether the item enters the project's ranked mean, or is reported on its own axis.</summary>
    public bool CountsTowardScore { get; init; } = true;
}

public sealed class BenchmarkTaskItemRecord
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid? ParentItemId { get; init; }

    public required int Index { get; init; }

    public required string Kind { get; init; }

    public required int Revision { get; init; }

    /// <summary>
    ///     Plaintext, and the value every run of this item is stamped with at freeze. The ranking read compares the two
    ///     without decrypting anything, so an edited item's stored answers are identifiable as answers to a question that
    ///     no longer exists.
    /// </summary>
    public required string InputHash { get; init; }

    public required bool CountsTowardScore { get; init; }

    public required ReadOnlyMemory<byte> PromptJson { get; init; }

    public required ReadOnlyMemory<byte>? ReferenceAnswerJson { get; init; }

    public required ReadOnlyMemory<byte>? VerifierConfigJson { get; init; }

    public required ReadOnlyMemory<byte>? GeneratorConfigJson { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    /// <summary>Whether a freeze fans out over this item, or it is only the generator of items that a freeze does.</summary>
    public bool IsLeaf => BenchmarkTaskItemKinds.IsLeaf(Kind);
}
