namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Caller request to ensure a specific GGUF file is present locally. When <see cref="FileName" /> is supplied it
///     selects the exact <c>.gguf</c>; otherwise <see cref="Quant" /> (defaulting to the store's configured default,
///     <c>Q4_K_M</c>) picks the matching quant from the repo's file list.
/// </summary>
public sealed record GgufModelRequest
{
    /// <summary>Hugging Face repository id, e.g. <c>bartowski/Llama-3.2-3B-Instruct-GGUF</c>.</summary>
    public required string RepoId { get; init; }

    /// <summary>Explicit <c>.gguf</c> filename to download; when <see langword="null" />, <see cref="Quant" /> selects.</summary>
    public string? FileName { get; init; }

    /// <summary>Quant label (e.g. <c>Q4_K_M</c>); when <see langword="null" />, the store applies its configured default.</summary>
    public string? Quant { get; init; }

    /// <summary>Git revision (commit SHA or branch) to pin; when <see langword="null" />, the repo default branch is resolved.</summary>
    public string? Revision { get; init; }

    /// <summary>Intended role hint recorded in the registry for the chat/embedding process split.</summary>
    public GgufRole Role { get; init; } = GgufRole.Unknown;
}

/// <summary>
///     A present (downloaded-and-verified) GGUF file: the local path plus the metadata the GGUF model store needs to
///     register and route the model. <see cref="Sha256" /> is <see langword="null" /> when the source LFS OID was not
///     exposed (revision-pin only).
/// </summary>
public sealed record GgufModelHandle(
    string ModelName,
    string LocalPath,
    string Quant,
    long SizeBytes,
    string? Sha256,
    string SourceRevision,
    GgufRole Role);

/// <summary>
///     On-disk registry manifest entry for one present GGUF file. The store is the only writer; the registry reads it.
/// </summary>
public sealed record GgufModelRegistryEntry
{
    /// <summary>Registry key — repo + quant identity, e.g. <c>bartowski/Llama-3.2-3B-Instruct-GGUF:Q4_K_M</c>.</summary>
    public required string ModelName { get; init; }

    /// <summary>Source Hugging Face repository id.</summary>
    public required string RepoId { get; init; }

    /// <summary>The downloaded <c>.gguf</c> filename.</summary>
    public required string FileName { get; init; }

    /// <summary>Parsed quant label.</summary>
    public required string Quant { get; init; }

    /// <summary>Absolute path to the verified local file.</summary>
    public required string LocalPath { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Verified sha256 (hash-pin) when the LFS OID was exposed; otherwise <see langword="null" />.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Resolved HF commit SHA the file was pulled at (revision-pin).</summary>
    public required string SourceRevision { get; init; }

    /// <summary>When the file completed downloading and verification.</summary>
    public required DateTimeOffset DownloadedAtUtc { get; init; }

    /// <summary>Role hint for the chat/embedding process split.</summary>
    public GgufRole Role { get; init; } = GgufRole.Unknown;
}

/// <summary>Sort order for GGUF repo discovery.</summary>
public enum GgufSearchSort
{
    /// <summary>Most downloaded first (lifetime cumulative downloads).</summary>
    Downloads = 0,

    /// <summary>Most liked first.</summary>
    Likes = 1,

    /// <summary>Most recently updated first.</summary>
    LastModified = 2,

    /// <summary>
    ///     Trending now — Hugging Face's recency-weighted popularity (the Hub "Trending" ranking, <c>sort=trendingScore</c>).
    ///     This is the freshness-aware default: lifetime <see cref="Downloads" /> is age-biased and surfaces years-old
    ///     repos, whereas trending reflects current download/like velocity.
    /// </summary>
    Trending = 3
}

/// <summary>Query parameters for searching GGUF repos on the Hugging Face Hub.</summary>
public sealed record GgufSearchQuery
{
    /// <summary>Free-text search term; when <see langword="null" /> the trending GGUF repos are returned.</summary>
    public string? SearchText { get; init; }

    /// <summary>Maximum repos to return.</summary>
    public int Limit { get; init; } = 30;

    /// <summary>Result ordering. Defaults to <see cref="GgufSearchSort.Trending" /> (freshness-aware) rather than lifetime downloads.</summary>
    public GgufSearchSort Sort { get; init; } = GgufSearchSort.Trending;
}

/// <summary>
///     Summary of a GGUF repo from a Hub search (popularity + gating + license). <see cref="IsTrustedPublisher" /> is a
///     soft quality signal (<see cref="GgufPublisherTrust" />) — a reputable packager / first-party org — never an
///     exclusion gate; untrusted repos still appear in results and are simply badged for review by the UI.
/// </summary>
public sealed record GgufRepoSummary(
    string RepoId,
    bool IsGated,
    long Downloads,
    int Likes,
    DateTimeOffset LastModified,
    string? License,
    bool HasUsableGguf,
    bool IsTrustedPublisher);

/// <summary>
///     One <c>.gguf</c> file inside a repo, with quant/size/integrity plus the GGUF header metadata read via an HTTP
///     range request during repo inspection (no full download). <strong>Frozen contract:</strong> the
///     <c>MemoryFitEstimator</c> consumes these header fields as a pure function and performs no GGUF parsing itself.
///     Header fields absent from a file are <see langword="null" />. <see cref="Sha256" /> is <see langword="null" />
///     when the LFS OID was not exposed (treat as "unavailable, revision-pin only").
/// </summary>
public sealed record GgufRepoFile(
    string FileName,
    string Quant,
    long SizeBytes,
    string? Sha256,
    string Revision,
    string? Architecture,
    string? QuantType,
    long? ParamCount,
    long? BlockCount,
    long? AttentionHeadCount,
    long? AttentionHeadCountKV,
    long? EmbeddingLength,
    long? ContextLength);

/// <summary>One repo's inspected detail: gating, license, and its usable <c>.gguf</c> files.</summary>
public sealed record GgufRepoDetail(
    string RepoId,
    bool IsGated,
    string? License,
    IReadOnlyList<GgufRepoFile> Files);

/// <summary>
///     The memory-footprint inputs for one INSTALLED GGUF model, sourced from the registry (quant label + on-disk file
///     size) plus a single tolerant header read (the estimator's weight/KV inputs). The quant label is the registry's
///     parsed value — never the header's stringified-int <c>general.file_type</c>. Header fields absent from the file are
///     <see langword="null" />; the consumer falls back to <see cref="FileSizeBytes" /> for the weights term when
///     <see cref="ParamCount" /> is null. This is the public seam the capacity footprint provider consumes so the GGUF
///     header reader can stay internal to the Hugging Face provider.
/// </summary>
public sealed record GgufModelFootprintFacts(
    string Quant,
    long FileSizeBytes,
    long? ParamCount,
    long? BlockCount,
    long? AttentionHeadCount,
    long? AttentionHeadCountKV,
    long? EmbeddingLength,
    long? ContextLength);
