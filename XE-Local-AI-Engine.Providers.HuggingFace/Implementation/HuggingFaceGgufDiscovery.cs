namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     <see cref="IHuggingFaceGgufDiscovery" /> over <see cref="HfHubClient" /> + <see cref="GgufHeaderReader" />:
///     searches GGUF repos by popularity (filtering out repos with no usable <c>.gguf</c>) and inspects a single repo's
///     actual files, populating per-file quant/size/integrity + GGUF header metadata.
/// </summary>
internal sealed class HuggingFaceGgufDiscovery : IHuggingFaceGgufDiscovery
{
    private const string GgufExtension = ".gguf";
    private readonly GgufHeaderReader _headerReader;

    private readonly HfHubClient _hubClient;
    private readonly ILogger<HuggingFaceGgufDiscovery> _logger;

    public HuggingFaceGgufDiscovery(HfHubClient hubClient,
        GgufHeaderReader headerReader,
        HuggingFaceOptions options,
        ILogger<HuggingFaceGgufDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(hubClient);
        ArgumentNullException.ThrowIfNull(headerReader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _hubClient = hubClient;
        _headerReader = headerReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GgufRepoSummary>> SearchAsync(GgufSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var models = await _hubClient.ListGgufModelsAsync(query, ct).ConfigureAwait(false);

        var summaries = new List<GgufRepoSummary>(models.Count);
        foreach (var model in models)
        {
            // A usable repo has at least one .gguf whose filename yields a recognizable quant token.
            var hasUsableGguf = model.FileNames.Any(IsUsableGgufFile);
            if (!hasUsableGguf)
            {
                continue;
            }

            summaries.Add(new GgufRepoSummary(model.RepoId,
                model.IsGated,
                model.Downloads,
                model.Likes,
                model.LastModified,
                model.License,
                HasUsableGguf: true));
        }

        return summaries;
    }

    /// <inheritdoc />
    public Task<GgufRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct)
    {
        return InspectCoreAsync(repoId, includeHeaderMetadata: true, ct);
    }

    /// <inheritdoc />
    public Task<GgufRepoDetail> ListRepoFilesAsync(string repoId, CancellationToken ct)
    {
        return InspectCoreAsync(repoId, includeHeaderMetadata: false, ct);
    }

    // Shared enumeration: lists a repo's usable, non-projector .gguf files; reads each file's GGUF header (a per-file
    // HTTP range request) only when includeHeaderMetadata is set. The header-free path backs interactive surfaces
    // (the quant picker) that need only quant + size, avoiding N sequential range reads.
    private async Task<GgufRepoDetail> InspectCoreAsync(string repoId, bool includeHeaderMetadata, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var detail = await _hubClient.GetRepoAsync(repoId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new GgufRepoDetail(repoId, IsGated: false, License: null, []);
        }

        var files = new List<GgufRepoFile>();
        foreach (var file in detail.Files)
        {
            // Single source of truth for "selectable model file": a real .gguf, not an mmproj projector companion, with
            // a containment-safe path and a recognizable quant token. Gating on the same predicate browse uses keeps the
            // picker from drifting from search/fit (a single unusable file is skipped, never repo-dropping).
            if (!IsUsableGgufFile(file.FileName))
            {
                _logger.LogDebug("Skipping a non-usable .gguf file during repo inspection.");
                continue;
            }

            // Non-null by IsUsableGgufFile (which requires a parseable quant); re-parsed here to capture the token.
            var quant = GgufQuantParser.TryParse(file.FileName)!;

            var header = includeHeaderMetadata
                ? await _headerReader.ReadHeaderAsync(detail.RepoId, file.FileName, detail.Revision, ct).ConfigureAwait(false)
                : null;

            files.Add(new GgufRepoFile(file.FileName,
                quant,
                file.SizeBytes,
                file.Sha256,
                detail.Revision,
                header?.Architecture,
                header?.QuantType,
                header?.ParamCount,
                header?.BlockCount,
                header?.AttentionHeadCount,
                header?.AttentionHeadCountKV,
                header?.EmbeddingLength,
                header?.ContextLength));
        }

        return new GgufRepoDetail(detail.RepoId, detail.IsGated, detail.License, files);
    }

    private static bool IsUsableGgufFile(string fileName)
    {
        return IsGgufFileName(fileName)
               && !IsProjectorFile(fileName)
               && GgufFilePath.IsSafeRelativePath(fileName)
               && GgufQuantParser.TryParse(fileName) is not null;
    }

    private static bool IsGgufFileName(string fileName)
    {
        return fileName.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase);
    }

    // HF/Unsloth multimodal projector companions are named like "mmproj-F16.gguf" / "*-mmproj-*.gguf". They are
    // matched anywhere in the file name (case-insensitive); no real quantized weight file carries the "mmproj" token.
    private static bool IsProjectorFile(string fileName)
    {
        return Path.GetFileName(fileName).Contains("mmproj", StringComparison.OrdinalIgnoreCase);
    }
}
