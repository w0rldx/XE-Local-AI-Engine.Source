namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

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
    /// <summary>Deterministic value-derived compare token. Legacy manifests may omit it on disk.</summary>
    public string? RegistryRevision { get; init; }

    /// <summary>Typed acquisition provenance; <see langword="null" /> only for legacy entries.</summary>
    public LocalModelOrigin? Origin { get; init; }

    /// <summary>Registry key — repo + quant identity, e.g. <c>bartowski/Llama-3.2-3B-Instruct-GGUF:Q4_K_M</c>.</summary>
    public required string ModelName { get; init; }

    public required string RepoId { get; init; }

    public required string FileName { get; init; }

    public required string Quant { get; init; }

    public required string LocalPath { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Verified sha256 (hash-pin) when the LFS OID was exposed; otherwise <see langword="null" />.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Resolved HF commit SHA the file was pulled at (revision-pin).</summary>
    public required string SourceRevision { get; init; }

    public required DateTimeOffset DownloadedAtUtc { get; init; }

    public GgufRole Role { get; init; } = GgufRole.Unknown;

    /// <summary>
    ///     The downloaded multimodal projector (<c>mmproj</c>) companion filename, when this repo shipped one and it was
    ///     pulled alongside the model; otherwise <see langword="null" />. A vision GGUF needs this projector for
    ///     llama-server to accept image input.
    /// </summary>
    public string? ProjectorFileName { get; init; }

    /// <summary>
    ///     Absolute path to the verified local multimodal projector (<c>mmproj</c>) file, or <see langword="null" /> when
    ///     the model has no projector companion. Passed to llama-server as <c>--mmproj</c> to enable image input.
    /// </summary>
    public string? ProjectorLocalPath { get; init; }

    /// <summary>Committed projector size, present together with <see cref="ProjectorSha256" />.</summary>
    public long? ProjectorSizeBytes { get; init; }

    /// <summary>Lowercase SHA-256 of committed projector bytes.</summary>
    public string? ProjectorSha256 { get; init; }

    /// <summary>Source basename only, never an absolute path or URL.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Recovery sidecar schema version for new acquisitions.</summary>
    public int? MetadataSchemaVersion { get; init; }

    /// <summary>Aggregate V1 content identity across weight and optional projector.</summary>
    public string? ModelContentFingerprint { get; init; }

    /// <summary>
    ///     Source Hugging Face repository the base checkpoint of a locally trained model came from, or
    ///     <see langword="null" /> for any entry that was not produced by a training run.
    /// </summary>
    public string? DerivedFromRepoId { get; init; }

    /// <summary>Resolved revision of <see cref="DerivedFromRepoId" />; <see langword="null" /> when that is.</summary>
    public string? DerivedFromRevision { get; init; }

    /// <summary>Frozen training-dataset content fingerprint the run consumed; <see langword="null" /> for non-trained entries.</summary>
    public string? DerivedFromContentFingerprint { get; init; }

    /// <summary>
    ///     The LoRA adapter file name when this entry IS an adapter rather than a standalone model. Its bytes are the
    ///     entry's own <see cref="LocalPath" /> (<see cref="FileName" /> equals this) — an adapter entry carries no
    ///     separate weight file, because its base weights live in the entry named by <see cref="BaseModelName" />.
    ///     <see langword="null" /> for a standalone model, including a merged fine-tune.
    /// </summary>
    public string? AdapterFileName { get; init; }

    /// <summary>Lowercase SHA-256 of the adapter bytes, present together with <see cref="AdapterFileName" />.</summary>
    public string? AdapterSha256 { get; init; }

    /// <summary>Adapter size in bytes, present together with <see cref="AdapterFileName" />.</summary>
    public long? AdapterSizeBytes { get; init; }

    /// <summary>Canonical member fingerprint over the adapter bytes, present together with <see cref="AdapterFileName" />.</summary>
    public string? AdapterMemberFingerprint { get; init; }

    /// <summary>
    ///     Registry name of the installed base model an adapter entry launches against — llama-server is given the base
    ///     model as <c>-m</c> and this entry's file as <c>--lora</c>. Required whenever <see cref="AdapterFileName" /> is
    ///     set; <see langword="null" /> otherwise.
    /// </summary>
    public string? BaseModelName { get; init; }
}

/// <summary>
///     A multimodal projector (<c>mmproj</c>) companion file inside a GGUF repo — the vision/audio encoder a vision
///     model needs for llama-server to accept image input. Discovered separately from the selectable weight files
///     (which exclude projectors), so a repo's projector can be paired with the chosen quant and downloaded alongside it.
/// </summary>
public sealed record GgufProjectorFile(
    string FileName,
    long SizeBytes,
    string? Sha256,
    string Revision);

/// <summary>Sort order for GGUF repo discovery.</summary>
public enum GgufSearchSort
{
    /// <summary>Most downloaded first (lifetime cumulative downloads).</summary>
    Downloads = 0,

    Likes = 1,

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
/// <param name="ExpertCount">
///     Total experts (GGUF <c>{arch}.expert_count</c>), when the header was read. A positive value marks the file as
///     Mixture-of-Experts; <see langword="null" /> for a dense model or when headers were not requested
///     (<c>ListRepoFilesAsync</c>). Feeds <c>MoeFacts.ExpertCount</c> for the memory-fit estimator's expert-offload split.
/// </param>
/// <param name="ExpertUsedCount">
///     Experts routed per token (GGUF <c>{arch}.expert_used_count</c>), when the header was read; <see langword="null" />
///     for a dense model or when headers were not requested.
/// </param>
/// <param name="AttentionKeyLength">
///     Explicit per-head key dimension (GGUF <c>{arch}.attention.key_length</c>), preferred by the memory-fit estimator
///     over the derived <c>head_dim = embedding_length / n_heads</c>; <see langword="null" /> when the header omits it.
/// </param>
/// <param name="AttentionValueLength">Explicit per-head value dimension (GGUF <c>{arch}.attention.value_length</c>), or <see langword="null" />.</param>
/// <param name="SlidingWindow">
///     Interleaved sliding-window-attention window size (GGUF <c>{arch}.attention.sliding_window</c>); a positive value
///     caps the window-limited layers' KV cache at the window in the estimator. <see langword="null" /> for a non-SWA model.
/// </param>
/// <param name="SlidingWindowPattern">
///     Global-attention layer stride (every Nth layer is full attention; Gemma3=6, Gemma2=2), resolved from the header or
///     a per-architecture default; <see langword="null" /> when unknown (the estimator then keeps every layer full-attention).
/// </param>
/// <param name="AttentionKeyLengthMla">
///     Multi-head Latent Attention latent key dimension (GGUF <c>{arch}.attention.key_length_mla</c>). Together with
///     <paramref name="AttentionValueLengthMla" /> this is llama.cpp's <c>is_mla()</c> test: when both are present and
///     positive the KV cache is one latent K tensor per layer and NO V tensor. <see langword="null" /> for every
///     non-MLA model. Detection is by these keys, never by architecture name.
/// </param>
/// <param name="AttentionValueLengthMla">
///     MLA latent value dimension (GGUF <c>{arch}.attention.value_length_mla</c>); present only alongside
///     <paramref name="AttentionKeyLengthMla" />. Under MLA no V cache is allocated, so this participates in detection
///     rather than in the byte formula.
/// </param>
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
    long? ContextLength,
    long? ExpertCount = null,
    long? ExpertUsedCount = null,
    long? AttentionKeyLength = null,
    long? AttentionValueLength = null,
    long? SlidingWindow = null,
    long? SlidingWindowPattern = null,
    long? AttentionKeyLengthMla = null,
    long? AttentionValueLengthMla = null);

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
    long? ContextLength,
    long? AttentionKeyLength = null,
    long? AttentionValueLength = null,
    long? SlidingWindow = null,
    long? SlidingWindowPattern = null,
    string? ContentIdentity = null,
    string? Architecture = null,
    long? ExpertCount = null,
    long? ExpertUsedCount = null,
    long? AttentionKeyLengthMla = null,
    long? AttentionValueLengthMla = null);
