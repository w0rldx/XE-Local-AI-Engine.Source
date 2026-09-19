namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     One Hub listing request. <see cref="Filter" /> is a <b>tag</b> facet (<c>gguf</c>) and
///     <see cref="PipelineTag" /> a <b>task</b> facet (<c>text-to-image</c>); either, both or neither may be set.
///     <see cref="Sort" /> is the raw Hub token (<c>trendingScore|downloads|likes|lastModified</c>).
/// </summary>
internal sealed record HubListQuery
{
    public string? Filter { get; init; }

    public string? PipelineTag { get; init; }

    public required string Sort { get; init; }

    public int Limit { get; init; } = 30;

    public string? SearchText { get; init; }
}

/// <summary>A repo as it appears in the Hub GGUF listing (filenames only, no per-file size).</summary>
internal sealed class HubModelSummary
{
    public required string RepoId { get; init; }

    public required bool IsGated { get; init; }

    public required long Downloads { get; init; }

    public required int Likes { get; init; }

    public required DateTimeOffset LastModified { get; init; }

    public required string? License { get; init; }

    public required IReadOnlyList<string> FileNames { get; init; }
}

/// <summary>One repo's inspected detail with per-file blob metadata.</summary>
internal sealed class HubModelDetail
{
    public required string RepoId { get; init; }

    public required bool IsGated { get; init; }

    public required string? License { get; init; }

    public required string Revision { get; init; }

    public required IReadOnlyList<HubRepoFile> Files { get; init; }
}

/// <summary>One sibling file's resolved size + optional LFS sha256.</summary>
internal sealed record HubRepoFile
{
    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required string? Sha256 { get; init; }
}
