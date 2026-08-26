namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class BenchmarkExportCsv
{
    private const string Header =
        "rank,modelGroupKey,model,quant,kvCacheType,flashAttention,backend,placement,contextTokens,status,stopReason,"
        + "repeatGroupId,repeatIndex,isWarmup,totalTokens,tokensPerSecond,ttftMs,promptTokens,promptTokensPerSecond,"
        + "generationTokens,generationTokensPerSecond,cachedPromptTokens,segmentCount,durationMs,qualityScore,qualityScoreSource,"
        + "judgeScore,userScore,rankExclusionReason,launchIdentity,receiptHash,"

        // APPENDED, never inserted. A CSV consumer that reads by column INDEX — which is most of them, and the whole
        // reason this export is flat — breaks silently on an inserted column and reads a seed as a token count.
        + "repeatMode,samplingSeed,samplingTemperature,"

        // Appended again, same rule. Quant fidelity is display-only and never a rank input; the pairwise columns carry
        // the fit's per-run interval. The strength itself is already in qualityScore with qualityScoreSource "pairwise".
        + "fidelityStatus,perplexityMean,perplexityStdErr,perplexityChunks,perplexityContextTokens,perplexityCorpusId,"
        + "kldState,kldMean,kldP99,topTokenAgreement,kldBaseFingerprint,kldBaseLogitsDigest,"
        + "pairwiseScore,pairwiseCiLow,pairwiseCiHigh,pairwiseComparisons,pairwiseFitKey,"
        + "taskItemId,taskItemIndex,cellKey,taskInputHash,taskItemSetHash,cellQuality";

    /// <param name="expectedKldBaseLogitsDigest">
    ///     The digest the project's CURRENT settings recompute. A run whose stored digest differs exports
    ///     <c>kldState=stale</c> with its KLD cells EMPTY — the same withholding the API does, because a number a
    ///     reader can still see is a number they will still compare.
    /// </param>
    public static string Render(IReadOnlyList<BenchmarkRunRecord> runs,
        string? expectedKldBaseLogitsDigest = null,
        BenchmarkPairwiseFitRecord? pairwiseFit = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var scores = BenchmarkExportPairwise.Scores(pairwiseFit);
        var builder = new StringBuilder(Header).Append("\r\n");
        foreach (var run in runs)
        {
            AppendRow(builder, run, expectedKldBaseLogitsDigest, pairwiseFit, scores);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     A value whose first character makes a spreadsheet read the cell as a formula. Several columns here are
    ///     operator-supplied (a model name, an HF repo id) or provider-verbatim (<c>stopReason</c>), so a row can carry
    ///     <c>=HYPERLINK(...)</c> into a workbook that evaluates it.
    /// </summary>
    private static readonly SearchValues<char> FormulaLeadCharacters = SearchValues.Create("=+-@\t\r");

    /// <summary>
    ///     Quoted only when it has to be, an embedded quote is doubled, and a value that would be read as a formula is
    ///     quoted with a leading apostrophe — the spreadsheet-standard text escape, which every reader strips back off.
    /// </summary>
    internal static string Field(string? value)
    {
        if (value is null or { Length: 0 })
        {
            return string.Empty;
        }

        if (FormulaLeadCharacters.Contains(value[0]))
        {
            return $"\"'{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void AppendRow(StringBuilder builder,
        BenchmarkRunRecord run,
        string? expectedKldBaseLogitsDigest,
        BenchmarkPairwiseFitRecord? pairwiseFit,
        IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> scores)
    {
        var intent = run.PrimaryLaunchIntent;
        var evidence = run.PrimaryLaunchEvidence;
        var placement = evidence?.PlacementOffloaded is { } offloaded && evidence.PlacementTotal is { } total
            ? $"{offloaded}/{total}"
            : string.Empty;
        builder.Append(Number(run.Rank))
               .Append(',')
               .Append(Field(BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin)))
               .Append(',')
               .Append(Field(run.PrimaryModelName))
               .Append(',')
               .Append(Field(BenchmarkModelGroupKey.QuantTag(run.PrimaryModelName)))
               .Append(',')
               .Append(Field(intent?.KvCacheType))
               .Append(',')
               .Append(Field(intent?.FlashAttentionMode))
               .Append(',')
               .Append(Field(evidence?.EffectiveBackend))
               .Append(',')
               .Append(placement)
               .Append(',')
               .Append(Number(run.EffectiveContextTokens ?? run.RequestedContextTokens))
               .Append(',')
               .Append(Field(JsonNamingPolicy.CamelCase.ConvertName(run.PrimaryStatus.ToString())))
               .Append(',')
               .Append(Field(run.PrimaryStopReason))
               .Append(',')
               .Append(run.RepeatGroupId?.ToString() ?? string.Empty)
               .Append(',')
               .Append(Number(run.RepeatIndex))
               .Append(',')
               .Append(run.IsWarmup ? "true" : "false")
               .Append(',')
               .Append(Number(run.TotalTokens))
               .Append(',')
               .Append(Rate(run.TokensPerSecond))
               .Append(',')
               .Append(Rate(run.Throughput?.TtftMs))
               .Append(',')
               .Append(Number(run.Throughput?.PromptTokens))
               .Append(',')
               .Append(Rate(run.Throughput?.PromptTokensPerSecond))
               .Append(',')
               .Append(Number(run.Throughput?.GenerationTokens))
               .Append(',')
               .Append(Rate(run.Throughput?.GenerationTokensPerSecond))
               .Append(',')
               .Append(Number(run.Throughput?.CachedPromptTokens))
               .Append(',')

               // Above 1 the token counts span several llama-server requests of one tool-calling turn, which is a
               // different fact from the same counts over a single prefill — and nothing else in the row says so.
               .Append(Number(run.Throughput?.SegmentCount))
               .Append(',')
               .Append(Number(run.DurationMs))
               .Append(',')
               .Append(Number(run.QualityScore))
               .Append(',')
               .Append(Field(run.QualityScoreSource ?? BenchmarkQualityScoreSources.None))
               .Append(',')
               .Append(Number(run.Judge?.Score))
               .Append(',')
               .Append(Number(run.UserScore))
               .Append(',')
               .Append(Field(run.Judge?.RankExclusionReason))
               .Append(',')
               .Append(Field(evidence?.EffectiveLaunchIdentity))
               .Append(',')
               .Append(Field(evidence?.ReceiptHash))
               .Append(',')
               .Append(Field(JsonNamingPolicy.CamelCase.ConvertName(run.RepeatMode.ToString())))
               .Append(',')

               // The one input that differs between the runs of an answer-variance group, so a reader can attribute
               // the spread without decrypting a snapshot.
               .Append(Field(run.SamplingSeed))
               .Append(',')
               .Append(Rate(run.SamplingTemperature))
               .Append(',');
        AppendFidelity(builder, run, expectedKldBaseLogitsDigest);
        AppendPairwise(builder, run, pairwiseFit, scores);
        _ = builder.Append(',')
                   .Append(Field(run.TaskItemId?.ToString()))
                   .Append(',')
                   .Append(Field(run.TaskItemIndex?.ToString(CultureInfo.InvariantCulture)))
                   .Append(',')
                   .Append(Field(run.CellKey))
                   .Append(',')
                   .Append(Field(run.TaskInputHash))
                   .Append(',')
                   .Append(Field(run.TaskItemSetHash))
                   .Append(',')
                   .Append(Number(run.CellQuality))
                   .Append("\r\n");
    }

    private static void AppendFidelity(StringBuilder builder, BenchmarkRunRecord run, string? expectedKldBaseLogitsDigest)
    {
        var fidelity = run.Fidelity;
        var comparable = fidelity is not null
                         && BenchmarkKldCacheKey.IsComparable(fidelity.KldBaseLogitsDigest, expectedKldBaseLogitsDigest);
        var measured = comparable ? BenchmarkFidelityKldStates.Ok : BenchmarkFidelityKldStates.Stale;
        var kldState = fidelity?.KldMean is null ? BenchmarkFidelityKldStates.None : measured;
        _ = builder.Append(Field(fidelity?.Status))
                   .Append(',')
                   .Append(Precise(fidelity?.PerplexityMean))
                   .Append(',')
                   .Append(Precise(fidelity?.PerplexityStdErr))
                   .Append(',')
                   .Append(Number(fidelity?.PerplexityChunks))
                   .Append(',')
                   .Append(Number(fidelity?.PerplexityContextTokens))
                   .Append(',')
                   .Append(Field(fidelity?.PerplexityCorpusId))
                   .Append(',')
                   .Append(Field(fidelity is null ? null : kldState))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.KldMean : null))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.KldP99 : null))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.TopTokenAgreement : null))
                   .Append(',')
                   .Append(Field(fidelity?.KldBaseFingerprint))
                   .Append(',')

                   // The digest is exported even when the figure is withheld: it is the EVIDENCE for the withholding,
                   // and a reader comparing it against the project's current one can see exactly what moved.
                   .Append(Field(fidelity?.KldBaseLogitsDigest))
                   .Append(',');
    }

    private static void AppendPairwise(StringBuilder builder,
        BenchmarkRunRecord run,
        BenchmarkPairwiseFitRecord? pairwiseFit,
        IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> scores)
    {
        _ = scores.TryGetValue(run.Id, out var entry);
        _ = builder.Append(Number(entry?.Score))
                   .Append(',')
                   .Append(Number(entry?.CiLow))
                   .Append(',')
                   .Append(Number(entry?.CiHigh))
                   .Append(',')
                   .Append(Number(entry?.Comparisons))
                   .Append(',')

                   // On every row of the cohort, not once at the top: a flat CSV has nowhere else to put it, and a
                   // reader filtering rows would otherwise lose which fit the numbers came from.
                   .Append(Field(entry is null ? null : pairwiseFit?.FitKey));
    }

    private static string Rate(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    ///     Six decimals, for the fidelity numbers. <see cref="Rate" />'s three are right for a token rate and wrong
    ///     here: the measured Q4_K_M/UD-Q3_K_XL perplexity gap is 6.7977 vs 6.9497 with standard errors around 0.074,
    ///     so rounding at three decimals throws away the digits that decide whether two quants separate at all.
    /// </summary>
    private static string Precise(double? value) =>
        value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
