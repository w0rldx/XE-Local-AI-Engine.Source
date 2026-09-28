namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Retrieval-quality metrics over one query group (overall, one category, or one language).</summary>
internal sealed record LiveMetricSummary
{
    public required int QueryCount { get; init; }

    public required int AnswerableCount { get; init; }

    public required int NoAnswerCount { get; init; }

    public required double? RecallAtK { get; init; }

    public required double? PrecisionAtK { get; init; }

    public required double? MeanReciprocalRank { get; init; }

    public required double? NdcgAtK { get; init; }

    public required double? CitationCoverage { get; init; }

    public required double? CitationAnchorRate { get; init; }

    public required double? SourceAnchorCoverage { get; init; }

    public required double? NoAnswerAccuracy { get; init; }

    /// <summary>
    ///     Macro-averages a group of per-query outcomes exactly as <see cref="RetrievalEvalHarness" /> does for the whole
    ///     set: quality over answerable queries, no-answer accuracy over no-answer queries. A metric with no query to
    ///     average over is null (reported n/a), never a misleading 0.
    /// </summary>
    public static LiveMetricSummary From(IReadOnlyCollection<QueryEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        var answerable = evaluations.Where(static evaluation => !evaluation.ExpectsNoAnswer).ToList();
        var noAnswer = evaluations.Where(static evaluation => evaluation.ExpectsNoAnswer).ToList();
        return new LiveMetricSummary
        {
            QueryCount = evaluations.Count,
            AnswerableCount = answerable.Count,
            NoAnswerCount = noAnswer.Count,
            RecallAtK = Mean(answerable, static evaluation => evaluation.RelevantDocumentCount == 0 ? 0d : (double)evaluation.RetrievedRelevantCount / evaluation.RelevantDocumentCount),
            PrecisionAtK = Mean(answerable, static evaluation => evaluation.PrecisionAtK),
            MeanReciprocalRank = Mean(answerable, static evaluation => evaluation.ReciprocalRank),
            NdcgAtK = Mean(answerable, static evaluation => evaluation.NdcgAtK),
            CitationCoverage = Mean(answerable, static evaluation => evaluation.CitationCoverage),
            CitationAnchorRate = Mean(answerable, static evaluation => evaluation.CitationAnchorPresent ? 1d : 0d),
            SourceAnchorCoverage = Mean(answerable, static evaluation => evaluation.SourceAnchorCoverage),
            NoAnswerAccuracy = Mean(noAnswer, static evaluation => evaluation.NoAnswerCorrect ? 1d : 0d)
        };
    }

    private static double? Mean(List<QueryEvaluation> evaluations, Func<QueryEvaluation, double> selector) =>
        evaluations.Count == 0 ? null : evaluations.Average(selector);
}

/// <summary>Nearest-rank latency distribution in milliseconds.</summary>
internal sealed record LiveLatency
{
    public required int Count { get; init; }

    public required double P50 { get; init; }

    public required double P95 { get; init; }

    public required double Max { get; init; }

    public static LiveLatency From(IEnumerable<double> milliseconds)
    {
        var ordered = milliseconds.Order().ToArray();
        return ordered.Length == 0
            ? new LiveLatency { Count = 0, P50 = 0d, P95 = 0d, Max = 0d }
            : new LiveLatency { Count = ordered.Length, P50 = Rank(ordered, 0.50d), P95 = Rank(ordered, 0.95d), Max = ordered[^1] };
    }

    private static double Rank(double[] ordered, double percentile) =>
        ordered[Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1)];
}

/// <summary>One measured configuration (a row of the plan's §4.3 table).</summary>
internal sealed record LiveConfigResult
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    /// <summary>False when the numbers must not be read as a quality result (a forced rerank that degraded, a failed sanity gate).</summary>
    public required bool Valid { get; init; }

    public string? InvalidReason { get; init; }

    public required LiveMetricSummary Overall { get; init; }

    public required IReadOnlyDictionary<string, LiveMetricSummary> ByCategory { get; init; }

    public required IReadOnlyDictionary<string, LiveMetricSummary> ByLanguage { get; init; }

    /// <summary>The English-only slice, where an English-only reranker is judged.</summary>
    public required LiveMetricSummary EnglishOnly { get; init; }

    /// <summary>
    ///     Chunk-boundary queries with both boundary phrases present in the top-K hit contents (no neighbor expansion),
    ///     over <see cref="BoundaryPairCount" />.
    /// </summary>
    public int BoundaryBothHalves { get; init; }

    /// <summary>Chunk-boundary queries that carry a boundary phrase pair.</summary>
    public int BoundaryPairCount { get; init; }

    public required LiveLatency EndToEnd { get; init; }

    public required LiveLatency RerankStage { get; init; }

    public int RerankCalls { get; init; }

    /// <summary>Largest pool one rerank call received (the service sends max(20, 4 x K) before dedupe).</summary>
    public int MaxRerankPool { get; init; }

    /// <summary>
    ///     Queries the adaptive gate kept from the reranker (queries minus rerank calls; gated reranker configs only). Not
    ///     split by reason: the gate's reason is not observable without a product change.
    /// </summary>
    public int? GateSkips { get; init; }

    public int RerankScored { get; init; }

    /// <summary>Logged degrades (<c>LogDegrade</c>), by reason.</summary>
    public IReadOnlyDictionary<string, int> Degrades { get; init; } = new Dictionary<string, int>();

    /// <summary>Queries whose query embedding failed, so the search fell back to lexical-only (any voids the row).</summary>
    public int QueryEmbeddingDegrades { get; init; }

    /// <summary>Rerank calls the search's latency budget cancelled (a fallback the client never logs).</summary>
    public int BudgetCancelled { get; init; }

    /// <summary>Reranker process spawn until its first <c>/health</c> 200 (PROD-cold only).</summary>
    public double? ColdSpawnToReadyMilliseconds { get; init; }

    /// <summary>End-to-end latency of the first query after the reranker became ready (PROD-cold only).</summary>
    public double? ColdFirstQueryMilliseconds { get; init; }

    /// <summary>
    ///     Rerank outcome of the same first query as <see cref="ColdFirstQueryMilliseconds" /> (cold PROD rows only): its
    ///     own rerank call's outcome, or "not reranked (adaptive gate)" when the gate skipped it.
    /// </summary>
    public string? ColdFirstQueryRerankOutcome { get; init; }

    /// <summary>
    ///     PROD rows only: latency of a 2-document probe rerank sent with no budget right after the row, i.e. how long the
    ///     work the row's budget cancelled kept the server busy.
    /// </summary>
    public double? BacklogDrainMilliseconds { get; init; }
}

/// <summary>Result of the Qwen3-style score sanity gate for one reranker.</summary>
internal sealed record LiveSanityResult
{
    public required string RerankerId { get; init; }

    public required bool Passed { get; init; }

    public required IReadOnlyList<string> PairScores { get; init; }
}

/// <summary>Result of one required negative control.</summary>
internal sealed class LiveControlResult
{
    public required string Name { get; init; }

    public required bool Passed { get; init; }

    public required string Detail { get; init; }
}

/// <summary>The contention round: chat throughput alone vs beside retrieval, or why it was skipped.</summary>
internal sealed record LiveContentionResult
{
    public string? SkippedReason { get; init; }

    public string? ChatModel { get; init; }

    public double? ChatSpawnToReadyMilliseconds { get; init; }

    public double? ChatTokensPerSecondBaseline { get; init; }

    public double? ChatTokensPerSecondUnderRetrieval { get; init; }

    public int ChatRequestsUnderRetrieval { get; init; }

    /// <summary>Set when the chat load stopped before the measurements finished; every @contention row is then INVALID.</summary>
    public string? LoadEndedEarly { get; init; }
}

internal sealed class LiveModelFile
{
    public required string Role { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }
}

/// <summary>One server the run launched: its launch context, cold start and footprint (VRAM delta is box-wide).</summary>
internal sealed class LiveServerFootprint
{
    public required string Name { get; init; }

    public required string Role { get; init; }

    public required int ContextTokens { get; init; }

    public required double SpawnToReadyMilliseconds { get; init; }

    public double? VramDeltaMiB { get; init; }

    public double? RssMiB { get; init; }
}

internal sealed record LiveEnvironment
{
    public required string CapturedAtUtc { get; init; }

    public required string Gpu { get; init; }

    public required string Cpu { get; init; }

    public required string RamTotal { get; init; }

    public required string LlamaServerVersion { get; init; }

    public required string LlamaServerPath { get; init; }

    public required int GpuLayers { get; init; }

    public required IReadOnlyList<LiveModelFile> Models { get; init; }

    public required string CorpusDirectory { get; init; }

    public required int CorpusFileCount { get; init; }

    /// <summary>SHA-256 over the sorted (relative path, file SHA-256) lines of every corpus file.</summary>
    public required string CorpusContentSha256 { get; init; }

    /// <summary>True when <c>git status --porcelain</c> listed changes: the numbers were measured on uncommitted code.</summary>
    public required bool GitDirty { get; init; }

    public required string GitHead { get; init; }

    public required int K { get; init; }
}

internal sealed record RetrievalEvalLiveReport
{
    public int SchemaVersion { get; init; } = 2;

    /// <summary>False when the run aborted; the configs measured before the abort are still reported.</summary>
    public required bool Completed { get; init; }

    public string? AbortReason { get; init; }

    public IReadOnlyList<LiveServerFootprint> Servers { get; init; } = [];

    /// <summary>
    ///     The product's embedding resolution (name, confident or not) and the stored chunk-vector width and identity,
    ///     checked against the production vector policy.
    /// </summary>
    public string? EmbeddingVectors { get; init; }

    public required LiveEnvironment Environment { get; init; }

    public required IReadOnlyList<LiveConfigResult> Configs { get; init; }

    public required IReadOnlyList<LiveSanityResult> Sanity { get; init; }

    public required IReadOnlyList<LiveControlResult> NegativeControls { get; init; }

    public LiveContentionResult? Contention { get; init; }
}

/// <summary>Writes the report as <c>retrieval-eval.json</c> + <c>retrieval-eval.md</c> and captures the environment block.</summary>
internal static class RetrievalEvalLiveReportWriter
{
    public const string JsonFileName = "retrieval-eval.json";
    public const string MarkdownFileName = "retrieval-eval.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(RetrievalEvalLiveReport report, string directory, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, JsonFileName), JsonSerializer.Serialize(report, JsonOptions) + "\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, MarkdownFileName), RenderMarkdown(report), cancellationToken);
    }

    public static string RenderMarkdown(RetrievalEvalLiveReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var env = report.Environment;
        var md = new StringBuilder();
        Line(md, "# Retrieval eval (real models)");
        Line(md, string.Empty);
        if (!report.Completed)
        {
            Line(md, $"**ABORTED:** {report.AbortReason} — only the configs below were measured.");
            Line(md, string.Empty);
        }

        Line(md, $"- Captured: {env.CapturedAtUtc} · git {env.GitHead}{(env.GitDirty ? " (dirty)" : string.Empty)} · K={F0(env.K)} · -ngl {F0(env.GpuLayers)}");
        Line(md, $"- GPU: {env.Gpu} · CPU: {env.Cpu} · RAM: {env.RamTotal}");
        Line(md, $"- llama-server: {env.LlamaServerVersion} ({env.LlamaServerPath})");
        Line(md, $"- Embedding: {report.EmbeddingVectors ?? "not reached"}");
        Line(md, $"- Corpus: {env.CorpusDirectory} ({F0(env.CorpusFileCount)} files, content sha256 `{env.CorpusContentSha256}`)");
        foreach (var model in env.Models)
        {
            Line(md, $"- {model.Role}: `{model.FileName}` {F0(model.SizeBytes)} B sha256 `{model.Sha256}`");
        }

        if (report.Servers.Count > 0)
        {
            Line(md, string.Empty);
            Line(md, "## Servers");
            Line(md, string.Empty);
            Line(md, "| server | role | -c | spawn→ready ms | VRAM Δ MiB (box-wide) | RSS MiB |");
            Line(md, "|---|---|---|---|---|---|");
            foreach (var server in report.Servers)
            {
                Line(md, $"| {server.Name} | {server.Role} | {F0(server.ContextTokens)} | {F1(server.SpawnToReadyMilliseconds)} | {F1(server.VramDeltaMiB)} | {F1(server.RssMiB)} |");
            }
        }

        Line(md, string.Empty);
        Line(md, "## Configurations");
        Line(md, string.Empty);
        Line(md, "n/a = no query to average over. noAns is structurally 0 today: there is no abstention threshold, so a no-answer query always gets hits. "
                 + "gate skips = queries the adaptive gate kept from the reranker (budget, arm agreement or too few candidates; the reason is not observable without a product change). "
                 + "`-full` reranks the whole pool (max pool shown), `-top10` only its top 10.");
        Line(md, string.Empty);
        Line(md, "| id | valid | R@K | P@K | MRR | nDCG@K | cite | anchor | noAns | e2e p50/p95/max ms | rerank p50/p95/max ms | scored/calls | max pool | gate skips | degrades | budget-cancel | query-embed degrades | cold ready/first ms |");
        Line(md, "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var config in report.Configs)
        {
            var o = config.Overall;
            var degrades = config.Degrades.Count == 0 ? "0" : string.Join(", ", config.Degrades.Select(static pair => $"{pair.Key}={F0(pair.Value)}"));
            var cold = config.ColdSpawnToReadyMilliseconds is null && config.ColdFirstQueryMilliseconds is null
                ? "-"
                : $"{F1(config.ColdSpawnToReadyMilliseconds)}/{F1(config.ColdFirstQueryMilliseconds)} ({config.ColdFirstQueryRerankOutcome ?? "-"})";
            Line(md, $"| {config.Id} | {(config.Valid ? "yes" : "INVALID: " + config.InvalidReason)} | {F3(o.RecallAtK)} | {F3(o.PrecisionAtK)} | {F3(o.MeanReciprocalRank)} | {F3(o.NdcgAtK)} | {F3(o.CitationCoverage)} | {F3(o.SourceAnchorCoverage)} | {F3(o.NoAnswerAccuracy)} | {Lat(config.EndToEnd)} | {Lat(config.RerankStage)} | {F0(config.RerankScored)}/{F0(config.RerankCalls)} | {F0(config.MaxRerankPool)} | {(config.GateSkips is { } skips ? F0(skips) : "-")} | {degrades} | {F0(config.BudgetCancelled)} | {F0(config.QueryEmbeddingDegrades)} | {cold} |");
        }

        var drained = report.Configs.Where(static config => config.BacklogDrainMilliseconds is not null).ToList();
        if (drained.Count > 0)
        {
            Line(md, string.Empty);
            Line(md, "## Backlog drain");
            Line(md, string.Empty);
            Line(md, "The client abandons a budget-cancelled rerank, the single-slot server does not. After each PROD row a 2-document probe rerank with no budget "
                     + "queues behind that leftover work: its latency (backlog drain ms) is how long cancelled work kept the reranker busy; an idle server answers in tens of ms.");
            Line(md, string.Empty);
            foreach (var config in drained)
            {
                Line(md, $"- {config.Id}: backlog drain {F1(config.BacklogDrainMilliseconds)} ms ({F0(config.BudgetCancelled)} budget-cancelled)");
            }
        }

        AppendGroupTable(md, report, "Per category (MRR / nDCG@K / R@K)", static config => config.ByCategory);
        AppendGroupTable(md, report, "Per language (MRR / nDCG@K / R@K)", static config => config.ByLanguage);

        Line(md, string.Empty);
        Line(md, "## EN-only slice");
        Line(md, string.Empty);
        Line(md, "English queries whose relevant documents are all English (cross-language and queries with a relevant `de/` document excluded).");
        Line(md, string.Empty);
        Line(md, "| id | n | R@K | P@K | MRR | nDCG@K | cite | anchor |");
        Line(md, "|---|---|---|---|---|---|---|---|");
        foreach (var config in report.Configs)
        {
            var en = config.EnglishOnly;
            Line(md, $"| {config.Id} | {F0(en.QueryCount)} | {F3(en.RecallAtK)} | {F3(en.PrecisionAtK)} | {F3(en.MeanReciprocalRank)} | {F3(en.NdcgAtK)} | {F3(en.CitationCoverage)} | {F3(en.SourceAnchorCoverage)} |");
        }

        Line(md, string.Empty);
        Line(md, "## Reranker sanity gate");
        Line(md, string.Empty);
        foreach (var sanity in report.Sanity)
        {
            Line(md, $"- {sanity.RerankerId}: {(sanity.Passed ? "passed" : "FAILED")} — {string.Join("; ", sanity.PairScores)}");
        }

        Line(md, string.Empty);
        Line(md, "## Negative controls");
        Line(md, string.Empty);
        foreach (var control in report.NegativeControls)
        {
            Line(md, $"- {control.Name}: {(control.Passed ? "passed" : "FAILED")} — {control.Detail}");
        }

        if (report.Contention is { } contention)
        {
            Line(md, string.Empty);
            Line(md, "## Contention");
            Line(md, string.Empty);
            if (contention.SkippedReason is not null)
            {
                Line(md, $"Skipped: {contention.SkippedReason}");
            }
            else
            {
                Line(md, $"- Chat model: `{contention.ChatModel}`, ready in {F1(contention.ChatSpawnToReadyMilliseconds)} ms");
                Line(md, $"- Chat tokens/s: baseline {F1(contention.ChatTokensPerSecondBaseline)}, under retrieval {F1(contention.ChatTokensPerSecondUnderRetrieval)} ({F0(contention.ChatRequestsUnderRetrieval)} requests)");
                if (contention.LoadEndedEarly is not null)
                {
                    Line(md, $"- INVALID: {contention.LoadEndedEarly}");
                }

                foreach (var loaded in report.Configs.Where(static config => config.Id.EndsWith(ContentionSuffix, StringComparison.Ordinal)))
                {
                    var quiet = report.Configs.FirstOrDefault(config => config.Id == loaded.Id[..^ContentionSuffix.Length]);
                    if (quiet is not null)
                    {
                        Line(md, $"- {quiet.Id}: rerank p50 {F1(quiet.RerankStage.P50)} → {F1(loaded.RerankStage.P50)} ms, p95 {F1(quiet.RerankStage.P95)} → {F1(loaded.RerankStage.P95)} ms");
                    }
                }
            }
        }

        return md.ToString();
    }

    /// <summary>Categories every config saturates: a reranker must not hurt them, but they carry no quality signal.</summary>
    public static readonly IReadOnlyList<string> CeilingCategories = ["code-exact-symbol", "code-path"];

    private const string ChunkBoundaryCategory = RetrievalEvalLiveCorpus.ChunkBoundaryCategory;

    /// <summary>Config id suffix for a re-run beside the chat load.</summary>
    public const string ContentionSuffix = "@contention";

    public static async Task<LiveEnvironment> CaptureEnvironmentAsync(RetrievalEvalLiveSettings settings,
        string corpusDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var models = new List<LiveModelFile>
        {
            await DescribeModelAsync("embedding", settings.EmbedModelPath, cancellationToken)
        };
        foreach (var reranker in settings.Rerankers)
        {
            models.Add(await DescribeModelAsync("reranker:" + reranker.Id, reranker.ModelPath, cancellationToken));
        }

        if (settings.ChatModelPath is { } chat)
        {
            models.Add(await DescribeModelAsync("chat", chat, cancellationToken));
        }

        var version = await RunToolAsync(settings.ServerPath, ["--version"], cancellationToken);
        return new LiveEnvironment
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            Gpu = await RunToolAsync("nvidia-smi", ["--query-gpu=name,memory.total,driver_version", "--format=csv,noheader"], cancellationToken),
            Cpu = ReadProcField("/proc/cpuinfo", "model name") ?? "unknown",
            RamTotal = ReadProcField("/proc/meminfo", "MemTotal") ?? "unknown",
            LlamaServerVersion = string.Join(" ", version.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(static line => line.StartsWith("version", StringComparison.Ordinal))),
            LlamaServerPath = settings.ServerPath,
            GpuLayers = settings.GpuLayers,
            Models = models,
            CorpusDirectory = corpusDirectory,
            CorpusFileCount = Directory.Exists(corpusDirectory) ? Directory.EnumerateFiles(corpusDirectory, "*", SearchOption.AllDirectories).Count() : 0,
            CorpusContentSha256 = await HashCorpusAsync(corpusDirectory, cancellationToken),
            GitHead = await RunToolAsync("git", ["-C", AppContext.BaseDirectory, "rev-parse", "HEAD"], cancellationToken),
            GitDirty = (await RunToolAsync("git", ["-C", AppContext.BaseDirectory, "status", "--porcelain"], cancellationToken)).Length > 0,
            K = settings.K
        };
    }

    /// <summary>
    ///     Runs a diagnostic tool with a bounded wait and returns its trimmed combined output, or an <c>unavailable</c>
    ///     marker: the environment block records what it could not read rather than failing the run over it.
    /// </summary>
    public static async Task<string> RunToolAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "unavailable";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // real-timer: bounds a diagnostic child process (nvidia-smi, git, llama-server --version) that has no event to gate on.
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or caller cancel: the child must not outlive either (no runner reaper when run directly).
                process.Kill(entireProcessTree: true);
                cancellationToken.ThrowIfCancellationRequested();
                return "unavailable (timed out)";
            }

            return ((await stdout) + (await stderr)).Trim();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return "unavailable (" + exception.Message + ")";
        }
    }

    /// <summary>SHA-256 over the ordinal-sorted "relative/path sha256" lines of every file under the corpus directory.</summary>
    public static async Task<string> HashCorpusAsync(string corpusDirectory, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        if (Directory.Exists(corpusDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(corpusDirectory, "*", SearchOption.AllDirectories))
            {
                await using var stream = File.OpenRead(file);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                lines.Add(Path.GetRelativePath(corpusDirectory, file).Replace('\\', '/') + " " + Convert.ToHexStringLower(hash));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static async Task<LiveModelFile> DescribeModelAsync(string role, string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new LiveModelFile
        {
            Role = role,
            FileName = Path.GetFileName(path),
            SizeBytes = stream.Length,
            Sha256 = Convert.ToHexStringLower(hash)
        };
    }

    private static string? ReadProcField(string path, string field)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var line = File.ReadLines(path).FirstOrDefault(candidate => candidate.StartsWith(field, StringComparison.Ordinal));
        return line?[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private static void AppendGroupTable(StringBuilder md,
        RetrievalEvalLiveReport report,
        string title,
        Func<LiveConfigResult, IReadOnlyDictionary<string, LiveMetricSummary>> select)
    {
        var groups = report.Configs.SelectMany(config => select(config).Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (groups.Count == 0)
        {
            return;
        }

        Line(md, string.Empty);
        Line(md, "## " + title);
        Line(md, string.Empty);
        if (groups.Exists(static group => CeilingCategories.Contains(group)))
        {
            Line(md, "`*` ceiling category (" + string.Join(", ", CeilingCategories) + "): a reranker must not hurt it; not a quality signal.");
        }

        if (groups.Contains(ChunkBoundaryCategory))
        {
            Line(md, $"`{ChunkBoundaryCategory}` also shows `both halves` = queries with both boundary phrases in the top-K hit contents (before neighbor expansion).");
        }

        Line(md, string.Empty);
        Line(md, "| id | " + string.Join(" | ", groups.Select(static group => CeilingCategories.Contains(group) ? group + " *" : group)) + " |");
        Line(md, "|---|" + string.Concat(groups.Select(static _ => "---|")));
        foreach (var config in report.Configs)
        {
            var cells = groups.Select(group => select(config).TryGetValue(group, out var summary)
                ? $"{F3(summary.MeanReciprocalRank)} / {F3(summary.NdcgAtK)} / {F3(summary.RecallAtK)} (n={F0(summary.QueryCount)}){BoundaryNote(config, group)}"
                : "-");
            Line(md, $"| {config.Id} | {string.Join(" | ", cells)} |");
        }
    }

    private static string BoundaryNote(LiveConfigResult config, string group) =>
        group == ChunkBoundaryCategory && config.BoundaryPairCount > 0
            ? $" · both halves {F0(config.BoundaryBothHalves)}/{F0(config.BoundaryPairCount)}"
            : string.Empty;

    private static string Lat(LiveLatency latency) =>
        latency.Count == 0 ? "-" : $"{F1(latency.P50)}/{F1(latency.P95)}/{F1(latency.Max)}";

    private static void Line(StringBuilder md, string text) =>
        md.Append(text).Append('\n');

    private static string F0(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string F1(double? value) =>
        value?.ToString("F1", CultureInfo.InvariantCulture) ?? "-";

    private static string F3(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";
}
