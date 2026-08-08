namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     <see cref="IHuggingFaceGgufDiscovery" /> over <see cref="HfHubClient" /> + <see cref="GgufHeaderReader" />:
///     searches GGUF repos by the requested order (trending by default; filtering out repos with no usable <c>.gguf</c>)
///     and inspects a single repo's actual files, populating per-file quant/size/integrity + GGUF header metadata. Each
///     summary is tagged with a soft <see cref="GgufPublisherTrust" /> publisher-trust flag (never an exclusion gate).
///     Two companion families are handled: an <c>mmproj</c> projector is dropped outright (<see cref="IsProjectorFile" />),
///     while a speculative-decoding drafter is KEPT but re-identified (<see cref="GgufDraftModel" />) — it is a real,
///     downloadable file the <c>draft-*</c> speculative modes need, it just is not a base-model quant.
/// </summary>
/// <remarks>
///     Considered-and-deferred perf idea (follow-up: revisit if inspection latency is still a problem after bounded
///     concurrency + caching): the model-fit advisor's quant-ladder walk (<c>GgufFileSelector.SelectBestFit</c>)
///     only needs each file's name + size to rank candidates, and reads only the winner's full GGUF header — in principle
///     <see cref="InspectRepoAsync" /> could defer ALL header reads until after ranking. Not implemented: the KV-cache term
///     in <c>MemoryFitEstimator.Estimate</c> depends on header-only fields (block/head counts, embedding length), so a
///     candidate's fits-the-budget verdict is header-dependent — deferring headers for non-winning candidates would change
///     which file the ladder walk picks in edge cases, not just how fast it gets there. Bounded concurrency (this file) +
///     TTL caching (<see cref="HfHubClient" />, <see cref="GgufHeaderReader" />) deliver the same latency win without that
///     behavior change.
/// </remarks>
internal sealed partial class HuggingFaceGgufDiscovery : IHuggingFaceGgufDiscovery
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

            // Non-null by IsUsableGgufFile (which requires a parseable quant); re-parsed here to capture the token. A
            // speculative-decoding drafter (an "MTP/" companion) parses to the SAME token as the base weights it drafts
            // for, so its quant is marked (Q8_0 → MTP-Q8_0) — otherwise the repo's quant list carries the label twice,
            // once for a 0.4 GB drafter and once for the 11.8 GB real model, and both map to the same registry key.
            var quant = GgufQuantParser.TryParse(file.FileName)!;
            usable.Add((file, GgufDraftModel.IsDraftFile(file.FileName) ? GgufDraftModel.MarkQuant(quant) : quant));
        }

        usable = GroupShards(usable);

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
                header?.ExpertUsedCount,
                header?.AttentionKeyLength,
                header?.AttentionValueLength,
                header?.SlidingWindow,
                header?.SlidingWindowPattern));
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

    // llama.cpp's split-GGUF naming convention for a model too large for one file: "<base>-00001-of-00003.gguf".
    // Only the FIRST split carries the full GGUF metadata header (architecture, context length, etc.); later
    // splits are raw tensor-data continuations with no header of their own and are never independently loadable.
    // Verified live 2026-07-10: Qwen/Qwen2.5-Coder-14B-Instruct-GGUF ships Q4_K_M as two splits (8.0GB + 0.99GB) —
    // treating them as independent candidates let the advisor pick the 0.99GB second split alone and under-estimate
    // a 14B model's footprint at ~1.8GB.
    [GeneratedRegex(@"^(?<base>.+)-(?<part>\d{5})-of-(?<total>\d{5})\.gguf$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ShardSuffixRegex();

    // Collapses each split-GGUF group into ONE candidate (representative = the lowest-numbered split, size = the
    // sum of every split in the group) and drops a split group entirely when a merged single-file variant of the
    // same quant is also present (dedupe by quant, prefer the non-sharded file). Non-split files pass through
    // untouched. Applied once, right after usability filtering, so every consumer of GgufRepoFile
    // (ListRepoFilesAsync, InspectRepoAsync, and — through them — the advisor and the model catalog) sees one
    // candidate per logical model+quant, never a bare, unloadable split fragment.
    private static List<(HfHubClient.HubRepoFile File, string Quant)> GroupShards(List<(HfHubClient.HubRepoFile File, string Quant)> usable)
    {
        var plain = new List<(HfHubClient.HubRepoFile File, string Quant)>();
        var shardGroups = new Dictionary<string, List<(HfHubClient.HubRepoFile File, string Quant, string Part)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in usable)
        {
            var match = ShardSuffixRegex().Match(entry.File.FileName);
            if (!match.Success)
            {
                plain.Add(entry);
                continue;
            }

            var baseName = match.Groups["base"].Value;
            if (!shardGroups.TryGetValue(baseName, out var group))
            {
                group = [];
                shardGroups[baseName] = group;
            }

            group.Add((entry.File, entry.Quant, match.Groups["part"].Value));
        }

        if (shardGroups.Count == 0)
        {
            return usable;
        }

        // Fixed-width (5-digit) zero-padded part numbers sort correctly under ordinal string comparison, so no
        // int.Parse is needed to find the lowest-numbered (first) split.
        var plainQuants = plain.Select(static p => p.Quant).ToHashSet(StringComparer.Ordinal);
        var result = new List<(HfHubClient.HubRepoFile File, string Quant)>(plain.Count + shardGroups.Count);
        result.AddRange(plain);

        foreach (var group in shardGroups.Values)
        {
            var representative = group.OrderBy(static g => g.Part, StringComparer.Ordinal).First();
            if (plainQuants.Contains(representative.Quant))
            {
                // A merged single-file variant of the same quant already exists — prefer it, drop the split group.
                continue;
            }

            var totalSize = group.Sum(static g => g.File.SizeBytes);
            result.Add((representative.File with
            {
                SizeBytes = totalSize
            }, representative.Quant));
        }

        return result;
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
