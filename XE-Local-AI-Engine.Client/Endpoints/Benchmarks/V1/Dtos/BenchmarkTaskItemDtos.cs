namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;

/// <summary>
///     One task item as an operator writes it. The index, the revision and the input hash are absent on purpose: the
///     server owns them, because a client that could name them could present an answer to an old question as an
///     answer to the current one.
/// </summary>
public class BenchmarkTaskItemMutationRequest
{
    public string Prompt { get; init; } = string.Empty;

    /// <summary>
    ///     Omitted means <c>prompt</c>. <c>niah</c> writes a long-context probe, which expands into its
    ///     <c>niahCase</c> children here and now, so each case is an ordinary item with its own id. A <c>niahCase</c>
    ///     is refused: a case is written by the generator that owns it, never by hand.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>Overrides the judge policy's reference answer for this item only.</summary>
    public string? ReferenceAnswer { get; init; }

    /// <summary>Per-criterion overrides of the judge policy's verifier config, keyed by criterion id.</summary>
    public JsonElement? VerifierConfig { get; init; }

    /// <summary>
    ///     Generator parameters; null for a plain prompt, required for a <c>niah</c> probe:
    ///     <c>{contextTokens[], needleDepthPercent[], needleTemplate?, questionTemplate?, criterionId?, seed?,
    ///     countsTowardScore?}</c>. One case is generated per (length x depth) pair, and a length past the project's
    ///     context window is refused with both numbers named.
    /// </summary>
    public JsonElement? GeneratorConfig { get; init; }

    /// <summary>Whether this item enters the project's ranked mean, or is reported on its own axis.</summary>
    public bool CountsTowardScore { get; init; } = true;
}

public sealed class CreateBenchmarkTaskItemRequest : BenchmarkTaskItemMutationRequest
{
    public Guid ProjectId { get; init; }

    /// <summary>The project version this write is made against: adding an item changes what a freeze would produce.</summary>
    public long ExpectedProjectVersion { get; init; }
}

public sealed class UpdateBenchmarkTaskItemRequest : BenchmarkTaskItemMutationRequest
{
    public Guid ProjectId { get; init; }
    public Guid ItemId { get; init; }

    /// <summary>The ITEM's version, not the project's — an edit is a write to one item.</summary>
    public long ExpectedVersion { get; init; }
}

public sealed class DeleteBenchmarkTaskItemRequest
{
    public Guid ProjectId { get; init; }
    public Guid ItemId { get; init; }
    public long ExpectedVersion { get; init; }
}

/// <summary>
///     The whole new order, named at once. Listing every current item id is also the concurrency check: an item added
///     or deleted while the operator was dragging makes the two sets disagree and the reorder is refused.
/// </summary>
public sealed class ReorderBenchmarkTaskItemsRequest
{
    public Guid ProjectId { get; init; }
    public IReadOnlyList<Guid> ItemIds { get; init; } = [];
}

/// <param name="InputHash">
///     What this item asks, as a value. Every run of it is stamped with a copy at freeze, and a run whose stamp no
///     longer matches answered a question that no longer exists.
/// </param>
public sealed class BenchmarkTaskItemResponse
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }

    /// <summary>The generator this item was expanded from, or null for an authored item.</summary>
    public Guid? ParentItemId { get; init; }

    /// <summary>Display position. Not a scoring input, and deliberately not part of the project's item-set hash.</summary>
    public int Index { get; init; }

    public required string Kind { get; init; }
    public int Revision { get; init; }
    public required string InputHash { get; init; }

    /// <summary>Whether a freeze fans out over this item, or it only generates the items that a freeze does.</summary>
    public bool IsLeaf { get; init; }

    public bool CountsTowardScore { get; init; }
    public required string Prompt { get; init; }
    public string? ReferenceAnswer { get; init; }
    public JsonElement? VerifierConfig { get; init; }
    public JsonElement? GeneratorConfig { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }
}

public sealed class ListBenchmarkTaskItemsResponse
{
    public IReadOnlyList<BenchmarkTaskItemResponse> Items { get; init; } = [];

    /// <summary>
    ///     What the whole leaf set asks, as a value — ordered by the items' ids, so adding or deleting one moves it
    ///     and reordering does not. Null on a project whose items have never been written.
    /// </summary>
    public string? TaskItemSetHash { get; init; }

    /// <summary>The project version, so a client can make its next item write against the version it just read.</summary>
    public long ProjectVersion { get; init; }
}
