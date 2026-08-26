namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One task item as it is written. The store owns <c>Index</c>, <c>Revision</c> and <c>InputHash</c> — a caller
///     that could name them could present a stale answer as a current one.
/// </summary>
/// <param name="Id">Empty mints a new one. Named explicitly only so a generated case can be created deterministically.</param>
/// <param name="ParentItemId">The generator this item was expanded from, or null for an authored item.</param>
/// <param name="CountsTowardScore">Whether the item enters the project's ranked mean, or is reported on its own axis.</param>
public sealed record BenchmarkTaskItemInput(
    ReadOnlyMemory<byte> PromptJson,
    string Kind = BenchmarkTaskItemKinds.Prompt,
    ReadOnlyMemory<byte>? ReferenceAnswerJson = null,
    ReadOnlyMemory<byte>? VerifierConfigJson = null,
    ReadOnlyMemory<byte>? GeneratorConfigJson = null,
    Guid Id = default,
    Guid? ParentItemId = null,
    bool CountsTowardScore = true);

/// <param name="InputHash">
///     Plaintext, and the value every run of this item is stamped with at freeze. The ranking read compares the two
///     without decrypting anything, so an edited item's stored answers are identifiable as answers to a question that
///     no longer exists.
/// </param>
public sealed record BenchmarkTaskItemRecord(
    Guid Id,
    Guid ProjectId,
    Guid? ParentItemId,
    int Index,
    string Kind,
    int Revision,
    string InputHash,
    bool CountsTowardScore,
    ReadOnlyMemory<byte> PromptJson,
    ReadOnlyMemory<byte>? ReferenceAnswerJson,
    ReadOnlyMemory<byte>? VerifierConfigJson,
    ReadOnlyMemory<byte>? GeneratorConfigJson,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc)
{
    /// <summary>Whether a freeze fans out over this item, or it is only the generator of items that a freeze does.</summary>
    public bool IsLeaf => BenchmarkTaskItemKinds.IsLeaf(Kind);
}
/// <summary>
///     The <see cref="BenchmarkTaskItem.Kind" /> vocabulary. A LEAF kind is a run target; a generator kind is not —
///     it expands into leaf children at write time, and every cap counts the leaves.
/// </summary>
public static class BenchmarkTaskItemKinds
{
    /// <summary>An authored prompt. The default, and the only kind a project created before task items existed has.</summary>
    public const string Prompt = "prompt";

    /// <summary>A long-context generator: never a run target, expands into <see cref="NiahCase" /> children.</summary>
    public const string Niah = "niah";

    /// <summary>One materialized long-context probe. A leaf with its own durable identity.</summary>
    public const string NiahCase = "niahCase";

    /// <summary>Whether an item of this kind is frozen into runs, or is only the generator of items that are.</summary>
    public static bool IsLeaf(string? kind) =>
        !string.Equals(kind, Niah, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="kind" /> is a member of the vocabulary at all.</summary>
    public static bool IsKnown(string? kind) =>
        kind is Prompt or Niah or NiahCase;
}
