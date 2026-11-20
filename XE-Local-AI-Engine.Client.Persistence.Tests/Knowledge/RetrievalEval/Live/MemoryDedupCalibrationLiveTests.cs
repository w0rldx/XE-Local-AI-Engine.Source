namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     OPT-IN dedup threshold calibration: each real embedder embeds the raw <c>DedupPairs/</c> texts, the frozen rule
///     derives its threshold, and the held-out scenarios replay through the production decision. Writes JSON + Markdown.
/// </summary>
/// <remarks>
///     Run: a Release build, then <c>scripts/with-build-lock.sh -- dotnet test XE-Local-AI-Engine.Client.Persistence.Tests
///     --configuration Release --no-build --treenode-filter '/*/*/MemoryDedupCalibrationLiveTests/*'</c> with
///     <c>XE_DEDUP_CALIBRATION_SERVER</c> and <c>XE_DEDUP_CALIBRATION_EMBED_MODELS</c> set (REPORT and NGL are optional). A model failing the rule is a reported outcome; only an invalid run
///     (no server, a short or malformed embedding response) fails the test. Rule and data rules: <c>DedupPairs/README.md</c>.
/// </remarks>
[Category(TestCategories.ExternalInfra)]
public sealed class MemoryDedupCalibrationLiveTests
{
    public const string ServerVariable = "XE_DEDUP_CALIBRATION_SERVER";
    public const string EmbedModelsVariable = "XE_DEDUP_CALIBRATION_EMBED_MODELS";
    public const string ReportVariable = "XE_DEDUP_CALIBRATION_REPORT";
    public const string GpuLayersVariable = "XE_DEDUP_CALIBRATION_NGL";

    public const string JsonFileName = "dedup-calibration.json";
    public const string MarkdownFileName = "dedup-calibration.md";

    private const int EmbeddingBatchSize = 32;

    private static readonly TimeSpan PooledReadyTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Test]
    [NotInParallel]
    public async Task RealEmbedders_DeriveTheDedupThreshold_AndReplayTheHeldOutScenarios(CancellationToken cancellationToken)
    {
        var serverPath = Environment.GetEnvironmentVariable(ServerVariable);
        var rawModels = Environment.GetEnvironmentVariable(EmbedModelsVariable);
        if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(rawModels))
        {
            // Both gate variables are unique to this calibration, so a normal test run can never trip it by accident.
            Skip.Test($"Live dedup calibration: set {ServerVariable} and {EmbedModelsVariable} ('canonicalName=path;...').");
            return;
        }

        var models = ParseModels(rawModels);
        var gpuLayers = ParseGpuLayers(Environment.GetEnvironmentVariable(GpuLayersVariable));
        var reportVariable = Environment.GetEnvironmentVariable(ReportVariable);
        var reportDirectory = string.IsNullOrWhiteSpace(reportVariable)
            ? Path.Combine(Path.GetTempPath(), "xe-dedup-calibration-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
            : reportVariable;
        var data = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);
        var report = new DedupCalibrationReport
        {
            CapturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            LlamaServerPath = serverPath,
            LlamaServerVersion = (await RetrievalEvalLiveReportWriter.RunToolAsync(serverPath, ["--version"], cancellationToken)).Trim(),
            GpuLayers = gpuLayers,
            PairsSha256 = data.PairsSha256,
            HeldOutSha256 = data.HeldOutSha256
        };

        try
        {
            foreach (var (name, path) in models)
            {
                report.Models.Add(await CalibrateAsync(serverPath, name, path, gpuLayers, data, cancellationToken));
            }
        }
        catch (Exception exception)
        {
            report.AbortReason = exception is LiveInfraException ? $"infra: {exception.Message}" : $"{exception.GetType().Name}: {exception.Message}";
            throw;
        }
        finally
        {
            // Not the test token: an aborted run must still leave the models it finished on disk.
            _ = Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, JsonFileName), JsonSerializer.Serialize(report, ReportJsonOptions) + "\n", CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, MarkdownFileName), RenderMarkdown(report), CancellationToken.None);
            Console.WriteLine($"Dedup calibration report: {reportDirectory}");
        }

        AssertEx.Equal(models.Count, report.Models.Count, "every configured embedder must produce a calibration result.");
        AssertEx.True(File.Exists(Path.Combine(reportDirectory, JsonFileName)), $"the report must exist in {reportDirectory}.");
    }

    /// <summary>Parses <c>canonicalName=path;...</c>; a missing half, a duplicate name or a missing file throws.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseModels(string raw)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var name = separator > 0 ? entry[..separator].Trim() : string.Empty;
            var path = separator > 0 ? entry[(separator + 1)..].Trim() : string.Empty;
            if (name.Length == 0 || path.Length == 0)
            {
                throw new FormatException($"{EmbedModelsVariable} entry '{entry}' is not 'canonicalName=path'.");
            }

            if (result.Exists(existing => string.Equals(existing.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException($"{EmbedModelsVariable} names '{name}' twice.");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"{EmbedModelsVariable}: the GGUF for '{name}' does not exist.", path);
            }

            result.Add(KeyValuePair.Create(name, path));
        }

        return result.Count > 0 ? result : throw new FormatException($"{EmbedModelsVariable} names no model.");
    }

    private static int ParseGpuLayers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RetrievalEvalLiveSettings.DefaultGpuLayers;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new FormatException($"{GpuLayersVariable}='{raw}' is not an integer >= 0.");
    }

    private static async Task<DedupModelResult> CalibrateAsync(string serverPath,
        string name,
        string modelPath,
        int gpuLayers,
        MemoryDedupPairSet data,
        CancellationToken ct)
    {
        string sha256;
        long sizeBytes;
        await using (var stream = File.OpenRead(modelPath))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            sizeBytes = stream.Length;
        }

        // The canonical name selects the profile pooling exactly as the launcher does for an installed model.
        await using var server = await LiveLlamaServer.StartAsync(serverPath, modelPath, ModelRole.Embedding, gpuLayers, PooledReadyTimeout, ct, embeddingModelName: name);
        var profilePooling = EmbeddingModelProfile.For(name).Pooling;
        if (!string.Equals(server.Pooling, profilePooling, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{name}' launched with --pooling {server.Pooling ?? "(none)"}, but its profile pools with {profilePooling}.");
        }

        await using var supervisor = new LiveEndpointSupervisor();
        supervisor.Register(name, ModelRole.Embedding, server.BaseAddress);
        var provider = new LlamaServerLocalModelProvider(supervisor, Substitute.For<IGgufModelStore>(), TimeProvider.System);
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = name,
            ProviderName = LlamaServerProviderConstants.ProviderName
        });

        // Raw behaviour text, exactly what MemorySemanticDeduplicator embeds: no profile prefix.
        var texts = data.Pairs.SelectMany(static pair => new[]
                        {
                            pair.A,
                            pair.B
                        })
                        .Concat(data.Scenarios.SelectMany(static scenario => scenario.Existing.Concat(scenario.Candidates)).Select(static item => item.Behavior))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
        foreach (var batch in texts.Chunk(EmbeddingBatchSize))
        {
            var generated = await generator.GenerateAsync(batch, options: null, ct);
            if (generated.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"'{name}' returned {generated.Count.ToString(CultureInfo.InvariantCulture)} embeddings for {batch.Length.ToString(CultureInfo.InvariantCulture)} inputs.");
            }

            for (var index = 0; index < batch.Length; index++)
            {
                vectors[batch[index]] = generated[index].Vector;
            }
        }

        var dimensions = vectors.Values.First().Length;
        if (dimensions == 0 || vectors.Values.Any(vector => vector.Length != dimensions || HasNonFinite(vector.Span)))
        {
            throw new InvalidOperationException($"'{name}' returned an empty, ragged or non-finite embedding.");
        }

        return Evaluate(name, modelPath, sizeBytes, sha256, server.Pooling ?? profilePooling, dimensions, data, vectors);
    }

    private static bool HasNonFinite(ReadOnlySpan<float> vector)
    {
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return true;
            }
        }

        return false;
    }

    private static DedupModelResult Evaluate(string name,
        string modelPath,
        long sizeBytes,
        string sha256,
        string pooling,
        int dimensions,
        MemoryDedupPairSet data,
        Dictionary<string, ReadOnlyMemory<float>> vectors)
    {
        var cosines = data.Pairs.ToDictionary(static pair => pair, pair => (double)KnowledgeVectorMath.CosineScore(vectors[pair.A], vectors[pair.B]));
        var distributions = new List<DedupCosineDistribution>();
        foreach (var kind in MemoryDedupPairSet.PairKinds)
        {
            foreach (var language in MemoryDedupPairSet.Languages)
            {
                distributions.Add(Describe(kind, language, [.. cosines.Where(entry => entry.Key.Kind == kind && entry.Key.Language == language).Select(static entry => entry.Value)]));
            }
        }

        double[] duplicates = [.. cosines.Where(static entry => entry.Key.Kind == MemoryDedupPairSet.DuplicateCandidate).Select(static entry => entry.Value)];
        double[] nearMisses = [.. cosines.Where(static entry => entry.Key.Kind == MemoryDedupPairSet.NearMissCandidate).Select(static entry => entry.Value)];
        double[] negatives = [.. cosines.Where(static entry => entry.Key.Kind != MemoryDedupPairSet.DuplicateCandidate).Select(static entry => entry.Value)];
        var interval = MemoryDedupCalibrationRule.BootstrapInterval(negatives);
        var threshold = MemoryDedupCalibrationRule.RoundUpToStep(interval.Upper);
        var calibrationRecall = duplicates.Count(cosine => cosine >= threshold) / (double)duplicates.Length;

        var scenarios = new List<DedupScenarioOutcome>();
        foreach (var scenario in data.Scenarios)
        {
            var flagged = MemorySemanticDeduplicator.ClassifyCandidates(scenario.ToExisting(),
                scenario.ToCandidates(),
                [.. scenario.Existing.Select(item => vectors[item.Behavior])],
                [.. scenario.Candidates.Select(item => vectors[item.Behavior])],
                threshold);
            scenarios.Add(new DedupScenarioOutcome
            {
                Id = scenario.Id,
                Expected = [.. scenario.ExpectedDuplicateIndexes.Order()],
                Flagged = [.. flagged.DuplicateIndexes.Order()]
            });
        }

        var truePositives = scenarios.Sum(static outcome => outcome.Flagged.Count(outcome.Expected.Contains));
        var flaggedTotal = scenarios.Sum(static outcome => outcome.Flagged.Count);
        var expectedTotal = scenarios.Sum(static outcome => outcome.Expected.Count);
        double? precision = flaggedTotal > 0 ? truePositives / (double)flaggedTotal : null;
        var recall = expectedTotal > 0 ? truePositives / (double)expectedTotal : 0d;

        return new DedupModelResult
        {
            CanonicalName = name,
            FileName = Path.GetFileName(modelPath),
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            Pooling = pooling,
            Dimensions = dimensions,
            Distributions = distributions,
            AucDuplicateVsNearMiss = MemoryDedupCalibrationRule.Auc(duplicates, nearMisses),
            NegativeQuantilePoint = MemoryDedupCalibrationRule.Quantile(negatives, MemoryDedupCalibrationRule.NegativeQuantile),
            NegativeQuantileCiLower = interval.Lower,
            NegativeQuantileCiUpper = interval.Upper,
            Threshold = threshold,
            CalibrationRecall = calibrationRecall,
            HeldOutPrecision = precision,
            HeldOutRecall = recall,
            HeldOutTruePositives = truePositives,
            HeldOutFalsePositives = flaggedTotal - truePositives,
            HeldOutFalseNegatives = expectedTotal - truePositives,
            Scenarios = scenarios,
            Passed = MemoryDedupCalibrationRule.Passes(threshold, calibrationRecall, recall, precision)
        };
    }

    private static DedupCosineDistribution Describe(string kind, string language, double[] values) =>
        new()
        {
            Kind = kind,
            Language = language,
            Count = values.Length,
            Min = values.Min(),
            P5 = MemoryDedupCalibrationRule.Quantile(values, 0.05d),
            Median = MemoryDedupCalibrationRule.Quantile(values, 0.5d),
            P95 = MemoryDedupCalibrationRule.Quantile(values, 0.95d),
            Max = values.Max()
        };

    private static string RenderMarkdown(DedupCalibrationReport report)
    {
        var md = new StringBuilder();
        Line(md, "# Memory dedup calibration (real embedders)");
        Line(md, string.Empty);
        if (report.AbortReason is not null)
        {
            Line(md, $"**ABORTED:** {report.AbortReason}; only the models below were measured.");
            Line(md, string.Empty);
        }

        Line(md, $"- Captured: {report.CapturedAtUtc} · -ngl {report.GpuLayers.ToString(CultureInfo.InvariantCulture)}");
        Line(md, $"- llama-server: {report.LlamaServerVersion} ({report.LlamaServerPath})");
        Line(md, $"- Data: pairs.json sha256 `{report.PairsSha256}`, heldout.json sha256 `{report.HeldOutSha256}`");
        Line(md, $"- Rule: t = upper 95% bootstrap bound ({MemoryDedupCalibrationRule.BootstrapResamples.ToString(CultureInfo.InvariantCulture)} resamples, seed "
                 + $"{MemoryDedupCalibrationRule.BootstrapSeed.ToString(CultureInfo.InvariantCulture)}) of the 0.95 quantile of near-miss + unrelated cosines, rounded up to 0.005");
        Line(md, string.Empty);
        Line(md, "| model | sha256 | pooling | dims | AUC dup/near | q95 [CI] | t | calib recall | held-out P | held-out R | verdict |");
        Line(md, "|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var model in report.Models)
        {
            Line(md, $"| `{model.CanonicalName}` | `{model.Sha256[..12]}` | {model.Pooling} | {model.Dimensions.ToString(CultureInfo.InvariantCulture)} | {F(model.AucDuplicateVsNearMiss)} "
                     + $"| {F(model.NegativeQuantilePoint)} [{F(model.NegativeQuantileCiLower)}, {F(model.NegativeQuantileCiUpper)}] | {F(model.Threshold)} | {F(model.CalibrationRecall)} "
                     + $"| {(model.HeldOutPrecision is { } precision ? F(precision) : "n/a")} | {F(model.HeldOutRecall)} | {(model.Passed ? "pass" : "calibration failure")} |");
        }

        foreach (var model in report.Models)
        {
            Line(md, string.Empty);
            Line(md, $"## `{model.CanonicalName}` cosines");
            Line(md, string.Empty);
            Line(md, "| kind | lang | n | min | p5 | median | p95 | max |");
            Line(md, "|---|---|---|---|---|---|---|---|");
            foreach (var row in model.Distributions)
            {
                Line(md, $"| {row.Kind} | {row.Language} | {row.Count.ToString(CultureInfo.InvariantCulture)} | {F(row.Min)} | {F(row.P5)} | {F(row.Median)} | {F(row.P95)} | {F(row.Max)} |");
            }

            Line(md, string.Empty);
            Line(md, $"Held-out: TP {model.HeldOutTruePositives.ToString(CultureInfo.InvariantCulture)}, FP {model.HeldOutFalsePositives.ToString(CultureInfo.InvariantCulture)}, "
                     + $"FN {model.HeldOutFalseNegatives.ToString(CultureInfo.InvariantCulture)}.");
        }

        return md.ToString();
    }

    private static string F(double value) =>
        value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder md, string text) =>
        md.Append(text).Append('\n');
}

/// <summary>The frozen calibration rule's arithmetic: type-7 quantiles, the seeded percentile bootstrap, rounding, AUC and the verdict.</summary>
internal static class MemoryDedupCalibrationRule
{
    public const int BootstrapResamples = 2_000;
    public const int BootstrapSeed = 20261117;
    public const double NegativeQuantile = 0.95d;
    public const double ThresholdStep = 0.005d;
    public const double MinimumRecall = 0.5d;
    public const double MinimumHeldOutPrecision = 0.9d;

    /// <summary>The linear-interpolation (type 7) quantile of <paramref name="values" /> at <paramref name="q" /> in [0, 1].</summary>
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        double[] sorted = [.. values.Order()];
        var position = (sorted.Length - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    /// <summary>The 95% percentile-bootstrap interval of the <see cref="NegativeQuantile" /> quantile, resampled with replacement under the fixed seed.</summary>
    public static DedupConfidenceInterval BootstrapInterval(IReadOnlyList<double> values)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);

        // Fixed seed by design: the seeded constructor keeps the legacy sequence, so the pre-registered rule reproduces exactly.
        var random = new Random(BootstrapSeed);
        var statistics = new double[BootstrapResamples];
        var sample = new double[values.Count];
        for (var resample = 0; resample < BootstrapResamples; resample++)
        {
            for (var index = 0; index < sample.Length; index++)
            {
                sample[index] = values[random.Next(values.Count)];
            }

            statistics[resample] = Quantile(sample, NegativeQuantile);
        }

        return new DedupConfidenceInterval
        {
            Lower = Quantile(statistics, 0.025d),
            Upper = Quantile(statistics, 0.975d)
        };
    }

    /// <summary>Rounds up to the next <see cref="ThresholdStep" />; an exact step stays, ignoring binary noise below 1e-9.</summary>
    public static double RoundUpToStep(double value) =>
        Math.Round(Math.Ceiling(Math.Round(value / ThresholdStep, 9)) * ThresholdStep, 3);

    /// <summary>The probability that a random positive scores above a random negative, ties counting half (Mann-Whitney).</summary>
    public static double Auc(IReadOnlyList<double> positives, IReadOnlyList<double> negatives)
    {
        ArgumentOutOfRangeException.ThrowIfZero(positives.Count);
        ArgumentOutOfRangeException.ThrowIfZero(negatives.Count);
        var wins = 0d;
        foreach (var positive in positives)
        {
            foreach (var negative in negatives)
            {
                if (positive > negative)
                {
                    wins += 1d;
                }
                else if (!(positive < negative))
                {
                    wins += 0.5d;
                }
            }
        }

        return wins / (positives.Count * (double)negatives.Count);
    }

    /// <summary>True when the model ships its threshold: t at most 1, both recalls at least 0.5 and held-out precision at least 0.9.</summary>
    public static bool Passes(double threshold, double calibrationRecall, double heldOutRecall, double? heldOutPrecision) =>
        threshold <= 1d && calibrationRecall >= MinimumRecall && heldOutRecall >= MinimumRecall && heldOutPrecision >= MinimumHeldOutPrecision;
}

/// <summary>A two-sided percentile-bootstrap interval; a record so two runs compare by value.</summary>
internal sealed record DedupConfidenceInterval
{
    public required double Lower { get; init; }

    public required double Upper { get; init; }
}

/// <summary>The calibration run's report, written as <c>dedup-calibration.json</c>.</summary>
internal sealed class DedupCalibrationReport
{
    public required string CapturedAtUtc { get; init; }

    public required string LlamaServerPath { get; init; }

    public required string LlamaServerVersion { get; init; }

    public required int GpuLayers { get; init; }

    public required string PairsSha256 { get; init; }

    public required string HeldOutSha256 { get; init; }

    public int BootstrapResamples { get; init; } = MemoryDedupCalibrationRule.BootstrapResamples;

    public int BootstrapSeed { get; init; } = MemoryDedupCalibrationRule.BootstrapSeed;

    public string? AbortReason { get; set; }

    public List<DedupModelResult> Models { get; } = [];
}

/// <summary>One embedder's distributions, derived threshold, held-out replay and verdict.</summary>
internal sealed class DedupModelResult
{
    public required string CanonicalName { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required string Pooling { get; init; }

    public required int Dimensions { get; init; }

    public required IReadOnlyList<DedupCosineDistribution> Distributions { get; init; }

    public required double AucDuplicateVsNearMiss { get; init; }

    public required double NegativeQuantilePoint { get; init; }

    public required double NegativeQuantileCiLower { get; init; }

    public required double NegativeQuantileCiUpper { get; init; }

    public required double Threshold { get; init; }

    public required double CalibrationRecall { get; init; }

    public double? HeldOutPrecision { get; init; }

    public required double HeldOutRecall { get; init; }

    public required int HeldOutTruePositives { get; init; }

    public required int HeldOutFalsePositives { get; init; }

    public required int HeldOutFalseNegatives { get; init; }

    public required IReadOnlyList<DedupScenarioOutcome> Scenarios { get; init; }

    public required bool Passed { get; init; }
}

/// <summary>Cosine summary of one pair kind in one language.</summary>
internal sealed class DedupCosineDistribution
{
    public required string Kind { get; init; }

    public required string Language { get; init; }

    public required int Count { get; init; }

    public required double Min { get; init; }

    public required double P5 { get; init; }

    public required double Median { get; init; }

    public required double P95 { get; init; }

    public required double Max { get; init; }
}

/// <summary>The production decision for one held-out scenario at the derived threshold.</summary>
internal sealed class DedupScenarioOutcome
{
    public required string Id { get; init; }

    public required IReadOnlyList<int> Expected { get; init; }

    public required IReadOnlyList<int> Flagged { get; init; }
}
