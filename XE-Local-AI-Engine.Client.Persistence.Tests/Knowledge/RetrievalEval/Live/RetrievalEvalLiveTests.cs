namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     OPT-IN real-model retrieval eval (PLAN retrieval-eval-2026-09-27 §4): real llama-server embedder and rerankers,
///     REAL vectors, the §4.3 configurations through the real search service and clients. Writes JSON + Markdown.
/// </summary>
/// <remarks>
///     Only the process supervisor is a stub, over servers this test owns (<see cref="KnowledgeSearchService" />,
///     <see cref="LlamaServerRerankerClient" /> and <see cref="LlamaServerLocalModelProvider" /> are real). Fails only
///     when a negative control fails (a bad reranker went unseen, a degraded forced rerank went undetected) or the run
///     is vacuous; INVALID configs are reported, never scored as quality.
/// </remarks>
[Category(TestCategories.ExternalInfra)]
public sealed class RetrievalEvalLiveTests : IDisposable
{
    private const string DeadRerankerName = "dead-endpoint";

    // Quality runs isolate model quality: adaptive gate off and a budget no rerank reaches.
    private const int UnboundedBudgetMilliseconds = 600_000;

    private const int ShuffleSeed = 0x5EED;

    private static readonly int ProductionBudgetMilliseconds = new KnowledgeBaseOptions().RetrievalLatencyBudgetMilliseconds;

    private static readonly TimeSpan PooledReadyTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ChatReadyTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Obviously relevant vs obviously irrelevant pairs: a broken reranker GGUF (llama.cpp #16407, Qwen3) returns
    ///     near-constant scores and fails at least one. A pair only gates a reranker that supports its language.
    /// </summary>
    private static readonly (string Language, string Query, string Relevant, string Irrelevant)[] SanityPairs =
    [
        ("en", "What is the capital of France?", "Paris is the capital and largest city of France.", "Mitochondria produce most of the chemical energy of a cell."),
        ("en", "How do I bake bread at home?", "Mix flour, water, yeast and salt, knead the dough, let it rise, then bake it at 230 degrees Celsius.",
            "The stock market closed higher on Tuesday after strong quarterly earnings."),
        ("de", "Wie hoch ist die Zugspitze?", "Die Zugspitze ist mit 2962 Metern der höchste Berg Deutschlands.", "Für den Apfelkuchen braucht man Zimt, Zucker und Butter."),
        ("en", "How does garbage collection work in .NET?",
            "The .NET garbage collector reclaims memory of unreachable objects and uses generations to collect short-lived objects cheaply.",
            "The Eiffel Tower was completed in 1889 for the World's Fair in Paris.")
    ];

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-retrieval-eval-" + Guid.NewGuid().ToString("N"));
    private readonly DegradeCapturingLogger _degrades = new();
    private readonly QueryEmbeddingDegradeLogger _queryEmbeddingDegrades = new();
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    [NotInParallel]
    public async Task RealModels_MeasureFusionRerankAndProductionConfigs_WithNegativeControls(CancellationToken cancellationToken)
    {
        var settings = RetrievalEvalLiveSettings.FromEnvironment(Environment.GetEnvironmentVariable);
        if (settings is null)
        {
            // Both gate variables are unique to this eval, so a normal test run can never trip it by accident.
            Skip.Test($"Live retrieval eval: set {RetrievalEvalLiveSettings.ServerVariable} and {RetrievalEvalLiveSettings.EmbedModelVariable}, "
                      + "or run scripts/run-retrieval-eval-local.sh.");
            return;
        }

        var corpus = LiveCorpusView.Load();
        if (corpus.Queries.Count == 0 || corpus.Documents.Count == 0)
        {
            throw new InvalidOperationException("Refusing a vacuous run: the live corpus loaded zero documents or zero queries.");
        }

        var reportDirectory = settings.ReportDirectory ?? Path.Combine(Path.GetTempPath(), "xe-retrieval-eval-report-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var environment = await RetrievalEvalLiveReportWriter.CaptureEnvironmentAsync(settings, corpus.Directory, cancellationToken);
        var run = new RunRecord();
        string? abortReason = null;
        try
        {
            await RunRoundAsync(settings, corpus, run, cancellationToken);
        }
        catch (Exception exception)
        {
            // The runner maps a reason starting "infra: " to exit 5 (a server never came up) and every other abort to exit 1.
            abortReason = exception is LiveInfraException ? $"infra: {exception.Message}" : $"{exception.GetType().Name}: {exception.Message}";
            throw;
        }
        finally
        {
            var report = new RetrievalEvalLiveReport
            {
                Completed = abortReason is null,
                AbortReason = abortReason,
                Environment = environment,
                EmbeddingVectors = run.EmbeddingVectors,
                Servers = run.Servers,
                Configs = run.Configs,
                Sanity = run.Sanity,
                NegativeControls = run.Controls,
                Contention = run.Contention
            };

            // Not the test token: a cancelled or failed round must still leave the configs it finished on disk. While an
            // abort is propagating, a failed write is only logged so it cannot replace the exception that aborted the round.
            try
            {
                await RetrievalEvalLiveReportWriter.WriteAsync(report, reportDirectory, CancellationToken.None);
                Console.WriteLine($"Retrieval eval report: {reportDirectory}");
                Console.WriteLine(RetrievalEvalLiveReportWriter.RenderMarkdown(report));
            }
            catch (Exception exception) when (abortReason is not null)
            {
                Console.WriteLine($"Retrieval eval report could NOT be written to {reportDirectory}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        AssertEx.True(settings.Rerankers.Count == 0 || run.Controls.Count == 2, "both negative controls must run when a reranker is configured.");
        var failed = run.Controls.Where(static control => !control.Passed).ToList();
        AssertEx.Empty(failed,
            "a negative control failed, so this round cannot tell a bad reranker from a good one: "
            + string.Join("; ", failed.Select(static control => $"{control.Name}: {control.Detail}")) + $" (report: {reportDirectory})");
    }

    private async Task RunRoundAsync(RetrievalEvalLiveSettings settings, LiveCorpusView corpus, RunRecord record, CancellationToken ct)
    {
        // Before any server starts: a boundary label whose halves share one chunk would score both-halves for free.
        await corpus.VerifyBoundaryLabelsAsync(ct);
        _ = Directory.CreateDirectory(_rootPath);
        await using var supervisor = new LiveEndpointSupervisor();
        var reranker = new LlamaServerRerankerClient(supervisor, _http, _degrades);

        await using var embedServer = await LiveLlamaServer.StartAsync(settings.ServerPath, settings.EmbedModelPath, ModelRole.Embedding, settings.GpuLayers, PooledReadyTimeout, ct);
        record.Servers.Add(Footprint("embedding", ModelRole.Embedding, embedServer));

        // The name the model store installs the GGUF under, so the product resolver matches it CONFIDENTLY and applies
        // the same vector policy (Nomic v1.5 -> Matryoshka 512) production would.
        var embedName = InstalledModelName(settings.EmbedModelPath);
        supervisor.Register(embedName, ModelRole.Embedding, embedServer.BaseAddress);
        var store = Substitute.For<IGgufModelStore>();
        _ = store.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
            new LocalModelDescriptor
            {
                ModelName = embedName,
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = new FileInfo(settings.EmbedModelPath).Length,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = null,
                Capabilities = []
            }
        ]));
        var embeddingProvider = new LlamaServerLocalModelProvider(supervisor, store, TimeProvider.System);

        // Shipped KnowledgeBaseOptions defaults throughout: chunk window, vector mode and the configured embedding name,
        // which the resolver maps to the installed GGUF exactly as on a node.
        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "live.sqlite"),
            _keyHolder,
            corpus.Documents,
            embeddingProvider,
            new KnowledgeBaseOptions(),
            LiveCorpusView.SourcePathOf,
            ct);
        var resolution = await new EmbeddingModelResolver(Microsoft.Extensions.Options.Options.Create(new KnowledgeBaseOptions())).ResolveAsync(embeddingProvider, ct);
        record.EmbeddingVectors = $"resolved '{resolution.Name}' ({(resolution.IsConfident ? "confident" : "NOT confident: no vector policy applies")}); "
                                  + await RequireProductionVectorPolicyAsync(fixture, embedName, ct);

        var runner = new ConfigRunner(fixture, corpus, settings.K, _degrades, _queryEmbeddingDegrades);
        var noReranker = Substitute.For<IRerankerClient>();
        var f0 = await runner.RunAsync("F0", "fusion only, ScoreAware (shipped default)", Options(string.Empty, adaptive: false, UnboundedBudgetMilliseconds), noReranker,
            forced: false, cold: false, ct);
        record.Configs.Add(f0);
        record.Configs.Add(await runner.RunAsync("F0-rrf", "fusion only, classic RRF", Options(string.Empty, adaptive: false, UnboundedBudgetMilliseconds, RankFusionStrategy.Rrf),
            noReranker, forced: false, cold: false, ct));

        for (var index = 0; index < settings.Rerankers.Count; index++)
        {
            var model = settings.Rerankers[index];
            var isFirst = index == 0;
            if (isFirst)
            {
                await RunProductionSpawnAsync(settings, model, supervisor, reranker, runner, record, ct);
                await RunProductionPooledAsync(settings, model, supervisor, reranker, runner, record, ct);
            }

            // A budget-cancelled rerank keeps the single-slot server busy after the client gave up, so the sanity gate and
            // the forced rows get a fresh server no PROD row ever touched; on CPU that backlog timed out the gate.
            await using var server = await LiveLlamaServer.StartAsync(settings.ServerPath, model.ModelPath, ModelRole.Reranker, settings.GpuLayers, PooledReadyTimeout, ct);
            supervisor.Register(model.Id, ModelRole.Reranker, server.BaseAddress);
            record.Servers.Add(Footprint($"reranker:{model.Id} (sanity, forced{(isFirst ? ", NEG" : string.Empty)})", ModelRole.Reranker, server));

            var gate = await RunSanityGateAsync(reranker, model, ct);
            record.Sanity.Add(gate);
            var invalidReason = gate.Passed ? null : $"sanity gate failed for {model.Id} (relevant did not outscore irrelevant)";

            foreach (var (label, pool) in RerankPools(reranker))
            {
                var result = await runner.RunAsync($"{model.Id}-{label}", $"{model.Id}, forced rerank of the {label} pool (adaptive off, unbounded budget)",
                    Options(model.Id, adaptive: false, UnboundedBudgetMilliseconds), pool, forced: true, cold: false, ct);
                record.Configs.Add(Invalidate(result, invalidReason));
            }

            if (!isFirst)
            {
                continue;
            }

            // The PROD rows measure this reranker too: a failed sanity gate voids them as well.
            for (var row = 0; row < record.Configs.Count; row++)
            {
                if (record.Configs[row].Id.StartsWith("PROD-", StringComparison.Ordinal))
                {
                    record.Configs[row] = Invalidate(record.Configs[row], invalidReason);
                }
            }

            await RunNegativeControlsAsync(model, f0, supervisor, reranker, runner, record, ct);
        }

        if (settings.ChatModelPath is { } chatModel)
        {
            record.Contention = await RunContentionRoundAsync(settings, chatModel, supervisor, reranker, runner, record, ct);
        }
    }

    /// <summary>
    ///     The product's real cold path: the reranker is NOT running when the first search arrives, so the supervisor
    ///     spawns it inside that search's 500 ms budget, detached, and later searches reuse it once it is ready.
    /// </summary>
    private async Task RunProductionSpawnAsync(RetrievalEvalLiveSettings settings,
        LiveRerankerModel model,
        LiveEndpointSupervisor supervisor,
        IRerankerClient reranker,
        ConfigRunner runner,
        RunRecord record,
        CancellationToken ct)
    {
        supervisor.RegisterLazy(model.Id,
            ModelRole.Reranker,
            lifetime => LiveLlamaServer.StartAsync(settings.ServerPath, model.ModelPath, ModelRole.Reranker, settings.GpuLayers, PooledReadyTimeout, lifetime),
            ct);
        try
        {
            var spawn = await runner.RunAsync("PROD-spawn", $"{model.Id}, shipped defaults (adaptive on, 500 ms), reranker spawned on the first search inside its budget",
                Options(model.Id, adaptive: true, ProductionBudgetMilliseconds), reranker, forced: false, cold: true, ct);
            var (spawned, failure) = await supervisor.AwaitLazyAsync(model.Id, ModelRole.Reranker);
            if (spawned is not null)
            {
                record.Servers.Add(Footprint($"reranker:{model.Id} (PROD-spawn)", ModelRole.Reranker, spawned));
            }

            record.Configs.Add(WithSpawnOutcome(spawn, spawned, failure) with
            {
                BacklogDrainMilliseconds = spawned is null ? null : await MeasureBacklogDrainAsync(spawned.BaseAddress, ct)
            });
        }
        finally
        {
            // Kills the spawned server, backlog included, before the next group starts its own.
            await supervisor.ReleaseLazyAsync(model.Id, ModelRole.Reranker);
        }
    }

    /// <summary>
    ///     PROD-fresh then PROD-warm on one pre-spawned server of their own, as production reuses it; the server is killed
    ///     before the forced rows, so their budget-cancelled work never reaches another group.
    /// </summary>
    private async Task RunProductionPooledAsync(RetrievalEvalLiveSettings settings,
        LiveRerankerModel model,
        LiveEndpointSupervisor supervisor,
        IRerankerClient reranker,
        ConfigRunner runner,
        RunRecord record,
        CancellationToken ct)
    {
        await using var server = await LiveLlamaServer.StartAsync(settings.ServerPath, model.ModelPath, ModelRole.Reranker, settings.GpuLayers, PooledReadyTimeout, ct);
        supervisor.Register(model.Id, ModelRole.Reranker, server.BaseAddress);
        record.Servers.Add(Footprint($"reranker:{model.Id} (PROD-fresh, PROD-warm)", ModelRole.Reranker, server));

        // Fresh: the first query runs right after a spawn that happened OUTSIDE the search budget.
        var fresh = await runner.RunAsync("PROD-fresh", $"{model.Id}, shipped defaults (adaptive on, 500 ms), first query right after a pre-spawned server became ready",
            Options(model.Id, adaptive: true, ProductionBudgetMilliseconds), reranker, forced: false, cold: true, ct);
        record.Configs.Add(fresh with
        {
            ColdSpawnToReadyMilliseconds = server.SpawnToReady.TotalMilliseconds,
            BacklogDrainMilliseconds = await MeasureBacklogDrainAsync(server.BaseAddress, ct)
        });

        // The drain probe above waited out PROD-fresh's backlog, so warm starts on an idle server.
        var warm = await runner.RunAsync("PROD-warm", $"{model.Id}, shipped defaults (adaptive on, 500 ms), warm",
            Options(model.Id, adaptive: true, ProductionBudgetMilliseconds), reranker, forced: false, cold: false, ct);
        record.Configs.Add(warm with
        {
            BacklogDrainMilliseconds = await MeasureBacklogDrainAsync(server.BaseAddress, ct)
        });
    }

    /// <summary>
    ///     How long the server stays busy after a row: the client abandons a budget-cancelled rerank, the single-slot
    ///     server does not, so an unbudgeted 2-document probe queues behind the backlog. Bounded by the 10-minute timeout.
    /// </summary>
    private async Task<double> MeasureBacklogDrainAsync(Uri baseAddress, CancellationToken ct)
    {
        var (_, query, relevant, irrelevant) = SanityPairs[0];
        var body = new JsonObject
        {
            ["query"] = query,
            ["documents"] = new JsonArray(relevant, irrelevant)
        };
        var started = Stopwatch.GetTimestamp();
        using var response = await _http.PostAsJsonAsync(new Uri(baseAddress.AbsoluteUri + "/rerank"), body, ct);
        _ = response.EnsureSuccessStatusCode();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    ///     Folds the lazy spawn into the PROD-spawn row. A failed spawn reaches the reranker client as a runtime failure it
    ///     degrades on, so every query fell back to fusion order: the row is INVALID, not a valid production number.
    /// </summary>
    internal static LiveConfigResult WithSpawnOutcome(LiveConfigResult row, LiveLlamaServer? spawned, string? spawnFailure) =>
        Invalidate(row with
        {
            ColdSpawnToReadyMilliseconds = spawned?.SpawnToReady.TotalMilliseconds
        }, spawnFailure is null ? null : $"reranker spawn failed: {spawnFailure}");

    /// <summary>
    ///     Records how many queries of a row fell back to lexical-only because the query embedding failed; any such query
    ///     voids the row, since its numbers no longer measure the hybrid (or reranked) configuration.
    /// </summary>
    internal static LiveConfigResult WithQueryEmbeddingDegrades(LiveConfigResult row, int degradedQueries)
    {
        if (degradedQueries == 0)
        {
            return row;
        }

        var reason = string.Create(CultureInfo.InvariantCulture, $"query embedding degraded to lexical-only on {degradedQueries} queries");
        return row with
        {
            QueryEmbeddingDegrades = degradedQueries,
            Valid = false,
            InvalidReason = row.InvalidReason is null ? reason : $"{reason}; {row.InvalidReason}"
        };
    }

    private static async Task RunNegativeControlsAsync(LiveRerankerModel model,
        LiveConfigResult f0,
        LiveEndpointSupervisor supervisor,
        IRerankerClient reranker,
        ConfigRunner runner,
        RunRecord record,
        CancellationToken ct)
    {
        // (a) A forced rerank against a dead endpoint must be detected as INVALID.
        supervisor.Register(DeadRerankerName, ModelRole.Reranker, new Uri($"http://127.0.0.1:{LiveLlamaServer.ReserveLoopbackPort().ToString(CultureInfo.InvariantCulture)}/v1"));
        var dead = await runner.RunAsync("NEG-dead", "forced rerank against a dead endpoint (must be INVALID)",
            Options(DeadRerankerName, adaptive: false, UnboundedBudgetMilliseconds), reranker, forced: true, cold: false, ct);
        record.Configs.Add(dead);
        record.Controls.Add(new LiveControlResult
        {
            Name = "dead-endpoint detected INVALID",
            Passed = !dead.Valid,
            Detail = dead.InvalidReason ?? "reported valid"
        });

        // (b) Shuffled scores must measure below fusion alone.
        var shuffled = await runner.RunAsync("NEG-shuffle", $"{model.Id} scores shuffled across the pool (must score below F0)",
            Options(model.Id, adaptive: false, UnboundedBudgetMilliseconds), new ShuffledScoresReranker(reranker, ShuffleSeed), forced: true, cold: false, ct);
        record.Configs.Add(shuffled);
        var below = shuffled.Overall.MeanReciprocalRank < f0.Overall.MeanReciprocalRank && shuffled.Overall.NdcgAtK < f0.Overall.NdcgAtK;
        record.Controls.Add(new LiveControlResult
        {
            Name = "shuffled scores below F0",
            Passed = shuffled.Valid && below,
            Detail = (shuffled.Valid ? string.Empty : $"INVALID: {shuffled.InvalidReason}; ")
                     + string.Create(CultureInfo.InvariantCulture,
                         $"MRR {shuffled.Overall.MeanReciprocalRank:F3} vs F0 {f0.Overall.MeanReciprocalRank:F3}; nDCG {shuffled.Overall.NdcgAtK:F3} vs F0 {f0.Overall.NdcgAtK:F3}")
        });
    }

    /// <summary>
    ///     Fails unless ingestion stored what production stores: one vector shape and, for Nomic v1.5, Matryoshka-512
    ///     (<c>KnowledgeEmbeddingVectorPolicy</c>, CONFIDENT resolution only). Native width measures vectors no node ships.
    /// </summary>
    private static async Task<string> RequireProductionVectorPolicyAsync(RetrievalEvalFixture fixture, string embedName, CancellationToken ct)
    {
        // KnowledgeEmbeddingVectorPolicy.MatryoshkaAlgorithm, private to the product.
        const string matryoshkaAlgorithm = "layernorm-population-eps1e-5-truncate-l2:v1";
        var shapes = await fixture.ReadVectorShapesAsync(ct);
        var described = string.Join("; ", shapes.Select(static shape => $"dim {shape.Dim.ToString(CultureInfo.InvariantCulture)} identity {shape.Identity}"));
        if (shapes.Count != 1)
        {
            throw new InvalidOperationException($"Expected one stored vector shape after ingestion, found {shapes.Count.ToString(CultureInfo.InvariantCulture)}: {described}");
        }

        var isNomicV15 = embedName.Contains("nomic-embed-text-v1.5", StringComparison.OrdinalIgnoreCase);
        if (isNomicV15 && (shapes[0].Dim != 512 || !shapes[0].Identity.Contains(matryoshkaAlgorithm, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Nomic v1.5 must be stored under the Matryoshka-512 policy, as production does; stored: {described}");
        }

        return described;
    }

    private static IEnumerable<(string Label, IRerankerClient Pool)> RerankPools(IRerankerClient reranker)
    {
        yield return ("full", reranker);
        yield return ("top10", new TopNRerankPool(reranker, 10));
    }

    private static LiveConfigResult Invalidate(LiveConfigResult result, string? reason) =>
        reason is null || !result.Valid
            ? result
            : result with
            {
                Valid = false,
                InvalidReason = reason
            };

    private static LiveServerFootprint Footprint(string name, ModelRole role, LiveLlamaServer server) =>
        new()
        {
            Name = name,
            Role = role.ToString(),
            ContextTokens = server.ContextTokens,
            SpawnToReadyMilliseconds = server.SpawnToReady.TotalMilliseconds,
            VramDeltaMiB = server.VramDeltaMiB,
            RssMiB = server.RssMiB
        };

    /// <summary>The name the model store registered the GGUF under (its sidecar), else the file stem.</summary>
    private static string InstalledModelName(string modelPath)
    {
        var sidecar = modelPath + ".xe-model.json";
        if (File.Exists(sidecar))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            if (document.RootElement.TryGetProperty("ModelName", out var name) && name.GetString() is { Length: > 0 } installed)
            {
                return installed;
            }
        }

        return Path.GetFileNameWithoutExtension(modelPath);
    }

    private static KnowledgeBaseOptions Options(string rerankerModel, bool adaptive, int budgetMilliseconds, RankFusionStrategy fusion = RankFusionStrategy.ScoreAware) =>
        new()
        {
            RerankerModelName = rerankerModel,
            AdaptiveRerankingEnabled = adaptive,
            RetrievalLatencyBudgetMilliseconds = budgetMilliseconds,
            FusionStrategy = fusion
        };

    private static async Task<LiveSanityResult> RunSanityGateAsync(IRerankerClient reranker, LiveRerankerModel model, CancellationToken ct)
    {
        var passed = true;
        var gated = 0;
        var lines = new List<string>();
        foreach (var (language, query, relevant, irrelevant) in SanityPairs)
        {
            if (!model.Supports(language))
            {
                lines.Add($"'{query}' ({language}): skipped (unsupported language)");
                continue;
            }

            var scores = await reranker.RerankAsync(model.Id, query, [relevant, irrelevant], ct);
            var ok = scores is { Count: 2 } && scores[0] > scores[1];
            passed &= ok;
            gated++;
            lines.Add(scores is { Count: 2 }
                ? string.Create(CultureInfo.InvariantCulture, $"'{query}': relevant {scores[0]:F4} vs irrelevant {scores[1]:F4}")
                : $"'{query}': no scores (degraded)");
        }

        // A gate that scored no pair has proven nothing about the reranker.
        if (gated == 0)
        {
            passed = false;
            lines.Add("no sanity pair in a supported language");
        }

        return new LiveSanityResult
        {
            RerankerId = model.Id,
            Passed = passed,
            PairScores = lines
        };
    }

    private async Task<LiveContentionResult> RunContentionRoundAsync(RetrievalEvalLiveSettings settings,
        string chatModel,
        LiveEndpointSupervisor supervisor,
        IRerankerClient reranker,
        ConfigRunner runner,
        RunRecord record,
        CancellationToken ct)
    {
        // Headroom for the chat weights, the largest reranker as measured when it spawned (it is respawned beside the chat
        // model), and 2 GiB; the embedder is already resident, so free VRAM counts it.
        var modelMiB = new FileInfo(chatModel).Length / (1024d * 1024d);
        var rerankerMiB = record.Servers.Where(static server => server.Role == nameof(ModelRole.Reranker)).Select(static server => server.VramDeltaMiB ?? 0d).DefaultIfEmpty(0d).Max();
        var neededVramMiB = modelMiB + rerankerMiB + 2048d;
        var freeVramMiB = await ReadFreeVramMiBAsync(ct);
        var availableRamGiB = ReadAvailableRamGiB();
        if (freeVramMiB is null || freeVramMiB < neededVramMiB || availableRamGiB < 8d)
        {
            var reason = string.Create(CultureInfo.InvariantCulture,
                $"insufficient headroom: free VRAM {freeVramMiB?.ToString("F0", CultureInfo.InvariantCulture) ?? "unknown"} MiB (need {neededVramMiB:F0}: chat {modelMiB:F0} + reranker {rerankerMiB:F0} + 2048), available RAM {availableRamGiB:F1} GiB (need 8)");
            Console.WriteLine("Contention round SKIPPED: " + reason);
            return new LiveContentionResult
            {
                SkippedReason = reason,
                ChatModel = Path.GetFileName(chatModel)
            };
        }

        await using var chat = await LiveLlamaServer.StartAsync(settings.ServerPath, chatModel, ModelRole.Chat, settings.GpuLayers, ChatReadyTimeout, ct, chatContextTokens: 4096);
        record.Servers.Add(Footprint("chat", ModelRole.Chat, chat));

        // One untimed request warms the chat process (--no-warmup), then a short solo loop is the baseline.
        _ = await GenerateAsync(chat.BaseAddress, maxRequests: 1, ct);
        var (_, baselineTokensPerSecond) = await GenerateAsync(chat.BaseAddress, maxRequests: 4, ct);

        using var load = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loaded = GenerateAsync(chat.BaseAddress, maxRequests: int.MaxValue, load.Token);
        bool loadEndedEarly;
        try
        {
            for (var index = 0; index < settings.Rerankers.Count; index++)
            {
                var model = settings.Rerankers[index];
                await using var server = await LiveLlamaServer.StartAsync(settings.ServerPath, model.ModelPath, ModelRole.Reranker, settings.GpuLayers, PooledReadyTimeout, ct);
                supervisor.Register(model.Id, ModelRole.Reranker, server.BaseAddress);
                record.Servers.Add(Footprint($"reranker:{model.Id}{RetrievalEvalLiveReportWriter.ContentionSuffix} (sanity, forced{(index == 0 ? ", PROD-warm last" : string.Empty)})",
                    ModelRole.Reranker, server));
                var gate = await RunSanityGateAsync(reranker, model, ct);
                record.Sanity.Add(gate with
                {
                    RerankerId = model.Id + RetrievalEvalLiveReportWriter.ContentionSuffix
                });
                var invalidReason = gate.Passed ? null : $"sanity gate failed for {model.Id} beside the chat model";

                var full = await runner.RunAsync($"{model.Id}-full{RetrievalEvalLiveReportWriter.ContentionSuffix}", $"{model.Id}-full beside a generating chat model",
                    Options(model.Id, adaptive: false, UnboundedBudgetMilliseconds), reranker, forced: true, cold: false, ct);
                record.Configs.Add(Invalidate(full, invalidReason));
                if (index == 0)
                {
                    // Last on this server: its budget-cancelled backlog must not reach the gate or the forced row above.
                    var warm = await runner.RunAsync($"PROD-warm{RetrievalEvalLiveReportWriter.ContentionSuffix}", $"{model.Id} shipped defaults, warm, beside a generating chat model",
                        Options(model.Id, adaptive: true, ProductionBudgetMilliseconds), reranker, forced: false, cold: false, ct);
                    record.Configs.Add(Invalidate(warm, invalidReason) with
                    {
                        BacklogDrainMilliseconds = await MeasureBacklogDrainAsync(server.BaseAddress, ct)
                    });
                }
            }
        }
        finally
        {
            // Only our cancel may end the load: one that stopped on its own measured some rows beside an idle chat model.
            loadEndedEarly = loaded.IsCompleted;
            await load.CancelAsync();
        }

        var loadedRequests = 0;
        double? loadedTokensPerSecond = null;
        string? loadEnded = null;
        try
        {
            (loadedRequests, loadedTokensPerSecond) = await loaded;
            loadEnded = loadEndedEarly ? $"chat load ended before the measurement (after {loadedRequests.ToString(CultureInfo.InvariantCulture)} requests)" : null;
        }
        catch (Exception exception) when (loadEndedEarly)
        {
            loadEnded = $"chat load ended before the measurement ({exception.GetType().Name}: {exception.Message})";
        }

        if (loadEnded is not null)
        {
            Console.WriteLine("Contention round INVALID: " + loadEnded);
            for (var row = 0; row < record.Configs.Count; row++)
            {
                if (record.Configs[row].Id.EndsWith(RetrievalEvalLiveReportWriter.ContentionSuffix, StringComparison.Ordinal))
                {
                    record.Configs[row] = Invalidate(record.Configs[row], "chat load ended before the measurement");
                }
            }
        }

        return new LiveContentionResult
        {
            ChatModel = Path.GetFileName(chatModel),
            ChatSpawnToReadyMilliseconds = chat.SpawnToReady.TotalMilliseconds,
            ChatTokensPerSecondBaseline = baselineTokensPerSecond,
            ChatTokensPerSecondUnderRetrieval = loadedTokensPerSecond,
            ChatRequestsUnderRetrieval = loadedRequests,
            LoadEndedEarly = loadEnded
        };
    }

    /// <summary>
    ///     A bounded generation loop over <c>/v1/chat/completions</c>: at most <paramref name="maxRequests" /> requests,
    ///     stopped early by <paramref name="ct" />; an in-flight request cut by the stop is not counted.
    /// </summary>
    private async Task<(int Requests, double? TokensPerSecond)> GenerateAsync(Uri baseAddress, int maxRequests, CancellationToken ct)
    {
        var requests = 0;
        long tokens = 0;
        var seconds = 0d;
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Write a detailed history of suspension bridges, from the earliest rope bridges to modern steel designs."
            }),
            ["max_tokens"] = 128,
            ["temperature"] = 0
        };
        var uri = new Uri(baseAddress.AbsoluteUri + "/chat/completions");
        while (requests < maxRequests && !ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _http.PostAsJsonAsync(uri, body, ct);
                _ = response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                tokens += payload?["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;
                seconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
                requests++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        return (requests, seconds > 0 ? tokens / seconds : null);
    }

    private static async Task<double?> ReadFreeVramMiBAsync(CancellationToken ct)
    {
        var raw = await RetrievalEvalLiveReportWriter.RunToolAsync("nvidia-smi", ["--query-gpu=memory.free", "--format=csv,noheader,nounits"], ct);
        var first = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib) ? mib : null;
    }

    private static double ReadAvailableRamGiB()
    {
        var line = File.Exists("/proc/meminfo") ? File.ReadLines("/proc/meminfo").FirstOrDefault(static candidate => candidate.StartsWith("MemAvailable:", StringComparison.Ordinal)) : null;
        var kilobytes = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return double.TryParse(kilobytes, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) ? kb / (1024d * 1024d) : 0d;
    }

    /// <summary>Runs one configuration through the harness and folds in the rerank-stage observations.</summary>
    private sealed class ConfigRunner
    {
        private readonly RetrievalEvalFixture _fixture;
        private readonly LiveCorpusView _corpus;
        private readonly int _k;
        private readonly DegradeCapturingLogger _degrades;
        private readonly QueryEmbeddingDegradeLogger _queryEmbeddingDegrades;

        public ConfigRunner(RetrievalEvalFixture fixture, LiveCorpusView corpus, int k, DegradeCapturingLogger degrades, QueryEmbeddingDegradeLogger queryEmbeddingDegrades)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            ArgumentNullException.ThrowIfNull(corpus);
            ArgumentNullException.ThrowIfNull(degrades);
            ArgumentNullException.ThrowIfNull(queryEmbeddingDegrades);
            _fixture = fixture;
            _corpus = corpus;
            _k = k;
            _degrades = degrades;
            _queryEmbeddingDegrades = queryEmbeddingDegrades;
        }

        public async Task<LiveConfigResult> RunAsync(string id,
            string description,
            KnowledgeBaseOptions options,
            IRerankerClient reranker,
            bool forced,
            bool cold,
            CancellationToken ct)
        {
            var timing = new TimingReranker(reranker);
            _degrades.Reset();
            _queryEmbeddingDegrades.Reset();
            var recording = new RecordingSearchService(_fixture.CreateSearchService(options, timing, _queryEmbeddingDegrades), timing);
            var metrics = await RetrievalEvalHarness.EvaluateAsync(recording, _corpus.Queries, _fixture.DocumentIdsByKey, _k, ct);

            // A budget-cancelled call can still be unwinding after the service stopped waiting for it.
            await timing.DrainAsync();
            var (boundaryBothHalves, boundaryPairs) = CountBoundaryBothHalves(recording.HitContents);
            var calls = timing.Calls;
            var degraded = _degrades.Degrades;
            var budgetCancelled = calls.Count(static call => call.Outcome == RerankOutcome.BudgetCancelled);
            var scored = calls.Count(static call => call.Outcome == RerankOutcome.Scored);

            string? invalidReason = null;
            if (forced && calls.Count == 0)
            {
                invalidReason = "forced rerank never ran";
            }
            else if (forced && (degraded.Count > 0 || budgetCancelled > 0 || scored != calls.Count))
            {
                invalidReason = string.Create(CultureInfo.InvariantCulture, $"forced rerank degraded ({degraded.Count} logged, {budgetCancelled} budget-cancelled, {scored}/{calls.Count} scored)");
            }

            var gated = options.AdaptiveRerankingEnabled && !string.IsNullOrWhiteSpace(options.RerankerModelName);
            var first = metrics.PerQuery.Count > 0 ? metrics.PerQuery[0].ElapsedMilliseconds : (double?)null;
            return WithQueryEmbeddingDegrades(new LiveConfigResult
            {
                Id = id,
                Description = description,
                Valid = invalidReason is null,
                InvalidReason = invalidReason,
                Overall = LiveMetricSummary.From(metrics.PerQuery.ToList()),
                ByCategory = Group(metrics.PerQuery, static label => label.Category),
                ByLanguage = Group(metrics.PerQuery, static label => label.Language),
                EnglishOnly = LiveMetricSummary.From(metrics.PerQuery.Where(evaluation => _corpus.Labels[evaluation.QueryId].EnglishOnly).ToList()),
                BoundaryBothHalves = boundaryBothHalves,
                BoundaryPairCount = boundaryPairs,
                EndToEnd = LiveLatency.From(metrics.PerQuery.Select(static evaluation => evaluation.ElapsedMilliseconds)),
                RerankStage = LiveLatency.From(calls.Where(static call => call.Outcome == RerankOutcome.Scored).Select(static call => call.Milliseconds)),
                RerankCalls = calls.Count,
                MaxRerankPool = calls.Count == 0 ? 0 : calls.Max(static call => call.DocumentCount),
                GateSkips = gated ? metrics.PerQuery.Count - calls.Count : null,
                RerankScored = scored,
                Degrades = degraded.GroupBy(static degrade => degrade.Reason, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
                BudgetCancelled = budgetCancelled,
                ColdFirstQueryMilliseconds = cold ? first : null,
                ColdFirstQueryRerankOutcome = cold && metrics.PerQuery.Count > 0 ? recording.RerankOutcomeOf(0) : null
            }, _queryEmbeddingDegrades.ExceptionTypes.Count);
        }

        // The harness searches the queries in order, one call each, so the i-th recorded hit list belongs to the i-th query.
        private (int BothHalves, int Pairs) CountBoundaryBothHalves(IReadOnlyList<IReadOnlyList<string>> hitContents)
        {
            var both = 0;
            var pairs = 0;
            for (var index = 0; index < _corpus.Queries.Count; index++)
            {
                var phrases = _corpus.Labels[_corpus.Queries[index].Id].BoundaryPhrases;
                if (phrases.Count != 2)
                {
                    continue;
                }

                pairs++;
                var retrieved = string.Join(" \n ", hitContents[index].Take(_k).Select(RetrievalEvalLiveCorpus.Normalize));
                if (phrases.All(phrase => retrieved.Contains(RetrievalEvalLiveCorpus.Normalize(phrase), StringComparison.Ordinal)))
                {
                    both++;
                }
            }

            return (both, pairs);
        }

        private Dictionary<string, LiveMetricSummary> Group(IReadOnlyList<QueryEvaluation> evaluations, Func<LiveQueryLabel, string> key) =>
            evaluations.GroupBy(evaluation => key(_corpus.Labels[evaluation.QueryId]), StringComparer.Ordinal)
                       .ToDictionary(static group => group.Key, static group => LiveMetricSummary.From(group.ToList()), StringComparer.Ordinal);
    }
}

/// <summary>
///     Passes searches through and keeps, per call in call order, the hit contents (boundary-pair metric) and the range of
///     rerank calls the search started (per-query rerank outcome).
/// </summary>
internal sealed class RecordingSearchService : IKnowledgeSearchService
{
    public const string NotReranked = "not reranked (adaptive gate)";

    private readonly IKnowledgeSearchService _inner;
    private readonly TimingReranker _timing;
    private readonly List<IReadOnlyList<string>> _hitContents = [];
    private readonly List<(int First, int End)> _rerankCalls = [];

    public RecordingSearchService(IKnowledgeSearchService inner, TimingReranker timing)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timing);
        _inner = inner;
        _timing = timing;
    }

    public IReadOnlyList<IReadOnlyList<string>> HitContents => _hitContents;

    public async Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken)
    {
        var first = _timing.StartedCalls;
        var result = await _inner.SearchAsync(request, cancellationToken);
        _rerankCalls.Add((first, _timing.StartedCalls));
        _hitContents.Add([.. result.Results.Select(static hit => hit.Content)]);
        return result;
    }

    /// <summary>
    ///     The outcome of the rerank call the <paramref name="searchIndex" />-th search started, or <see cref="NotReranked" />
    ///     when it started none. Call after <see cref="TimingReranker.DrainAsync" />, so the call has recorded its outcome.
    /// </summary>
    public string RerankOutcomeOf(int searchIndex)
    {
        var (first, end) = _rerankCalls[searchIndex];
        return end == first ? NotReranked : _timing.Calls.Single(call => call.Sequence == first).Outcome.ToString();
    }
}

/// <summary>What one round measured so far; the report is written from it even when the round aborts.</summary>
internal sealed class RunRecord
{
    public List<LiveConfigResult> Configs { get; } = [];

    public List<LiveSanityResult> Sanity { get; } = [];

    public List<LiveControlResult> Controls { get; } = [];

    public List<LiveServerFootprint> Servers { get; } = [];

    public string? EmbeddingVectors { get; set; }

    public LiveContentionResult? Contention { get; set; }
}
