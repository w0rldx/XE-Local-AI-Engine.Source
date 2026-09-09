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
internal sealed record HubModelSummary(
    string RepoId,
    bool IsGated,
    long Downloads,
    int Likes,
    DateTimeOffset LastModified,
    string? License,
    IReadOnlyList<string> FileNames);

/// <summary>One repo's inspected detail with per-file blob metadata.</summary>
internal sealed record HubModelDetail(
    string RepoId,
    bool IsGated,
    string? License,
    string Revision,
    IReadOnlyList<HubRepoFile> Files);

/// <summary>One sibling file's resolved size + optional LFS sha256.</summary>
internal sealed record HubRepoFile(string FileName, long SizeBytes, string? Sha256);
