namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     <see cref="IHuggingFaceGgufDiscovery" /> over <see cref="HfHubClient" /> + <see cref="GgufHeaderReader" />:
///     searches GGUF repos by the requested order (trending by default; filtering out repos with no usable <c>.gguf</c>)
///     and inspects a single repo's actual files, populating per-file quant/size/integrity + GGUF header metadata. Each
///     summary is tagged with a soft <see cref="GgufPublisherTrust" /> publisher-trust flag (never an exclusion gate).
/// </summary>
/// <remarks>
///     Considered-and-deferred perf idea (follow-up: revisit if inspection latency is still a problem after bounded
///     concurrency + caching): the model-fit advisor's quant-ladder walk (<c>ModelFitRefreshService.SelectBestFittingFile</c>)
///     only needs each file's name + size to rank candidates, and reads only the winner's full GGUF header — in principle
///     <see cref="InspectRepoAsync" /> could defer ALL header reads until after ranking. Not implemented: the KV-cache term
///     in <c>MemoryFitEstimator.Estimate</c> depends on header-only fields (block/head counts, embedding length), so a
///     candidate's fits-the-budget verdict is header-dependent — deferring headers for non-winning candidates would change
///     which file the ladder walk picks in edge cases, not just how fast it gets there. Bounded concurrency (this file) +
///     TTL caching (<see cref="HfHubClient" />, <see cref="GgufHeaderReader" />) deliver the same latency win without that
///     behavior change.
/// </remarks>
internal sealed class HuggingFaceGgufDiscovery : IHuggingFaceGgufDiscovery
{
    private const string GgufExtension = ".gguf";
    private readonly GgufHeaderReader _headerReader;

    private readonly HfHubClient _hubClient;
    private readonly ILogger<HuggingFaceGgufDiscovery> _logger;
    private readonly HuggingFaceOptions _options;

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
        _options = options;
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
                HasUsableGguf: true,
                GgufPublisherTrust.IsTrustedPublisher(model.RepoId)));
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
    // (the quant picker) that need only quant + size, avoiding N range reads. When headers ARE requested, they are
    // fetched with bounded concurrency (ReadHeadersAsync) rather than one at a time — a repo can ship 10-25 quant
    // variants, and the header reads are independent per-file range requests with no ordering dependency.
    private async Task<GgufRepoDetail> InspectCoreAsync(string repoId, bool includeHeaderMetadata, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var detail = await _hubClient.GetRepoAsync(repoId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new GgufRepoDetail(repoId, IsGated: false, License: null, []);
        }

        var usable = new List<(HfHubClient.HubRepoFile File, string Quant)>();
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
            usable.Add((file, GgufQuantParser.TryParse(file.FileName)!));
        }

        var headers = includeHeaderMetadata
            ? await ReadHeadersAsync(detail.RepoId, detail.Revision, usable, ct).ConfigureAwait(false)
            : null;

        var files = new List<GgufRepoFile>(usable.Count);
        for (var i = 0; i < usable.Count; i++)
        {
            var (file, quant) = usable[i];
            var header = headers?[i];

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
                header?.ContextLength,
                header?.ExpertCount,
                header?.ExpertUsedCount));
        }

        return new GgufRepoDetail(detail.RepoId, detail.IsGated, detail.License, files);
    }

    // Reads every usable file's GGUF header with at most HeaderReadConcurrency in flight at once, preserving the
    // input order in the returned array (index i is usable[i]'s header) so the caller can zip them back together.
    private async Task<GgufHeaderMetadata[]> ReadHeadersAsync(string repoId,
        string revision,
        IReadOnlyList<(HfHubClient.HubRepoFile File, string Quant)> usable,
        CancellationToken ct)
    {
        var results = new GgufHeaderMetadata[usable.Count];
        var concurrency = Math.Max(val1: 1, _options.HeaderReadConcurrency);
        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var reads = usable.Select(async (entry, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await _headerReader.ReadHeaderAsync(repoId, entry.File.FileName, revision, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(reads).ConfigureAwait(false);
        return results;
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
