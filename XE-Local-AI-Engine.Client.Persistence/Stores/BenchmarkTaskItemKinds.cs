namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

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
