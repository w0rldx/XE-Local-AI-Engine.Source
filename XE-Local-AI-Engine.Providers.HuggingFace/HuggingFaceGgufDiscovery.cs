namespace XE_Local_AI_Engine.Providers.HuggingFace;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

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
                true));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<GgufRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var detail = await _hubClient.GetRepoAsync(repoId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new GgufRepoDetail(repoId, false, null, []);
        }

        var files = new List<GgufRepoFile>();
        foreach (var file in detail.Files)
        {
            if (!IsGgufFileName(file.FileName))
            {
                continue;
            }

            // Untrusted repo input: drop any name that could traverse outside the models directory once downloaded.
            if (!GgufFilePath.IsSafeRelativePath(file.FileName))
            {
                _logger.LogDebug("Skipping a .gguf file with an unsafe path during repo inspection.");
                continue;
            }

            var quant = GgufQuantParser.TryParse(file.FileName);
            if (quant is null)
            {
                // A single unparseable .gguf is skipped, never repo-dropping (tolerant per the discovery contract).
                _logger.LogDebug("Skipping a .gguf file with no recognizable quant token during repo inspection.");
                continue;
            }

            var header = await _headerReader
                               .ReadHeaderAsync(detail.RepoId, file.FileName, detail.Revision, ct)
                               .ConfigureAwait(false);

            files.Add(new GgufRepoFile(file.FileName,
                quant,
                file.SizeBytes,
                file.Sha256,
                detail.Revision,
                header.Architecture,
                header.QuantType,
                header.ParamCount,
                header.BlockCount,
                header.AttentionHeadCount,
                header.AttentionHeadCountKV,
                header.EmbeddingLength,
                header.ContextLength));
        }

        return new GgufRepoDetail(detail.RepoId, detail.IsGated, detail.License, files);
    }

    private static bool IsUsableGgufFile(string fileName)
    {
        return IsGgufFileName(fileName)
               && GgufFilePath.IsSafeRelativePath(fileName)
               && GgufQuantParser.TryParse(fileName) is not null;
    }

    private static bool IsGgufFileName(string fileName)
    {
        return fileName.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase);
    }
}
