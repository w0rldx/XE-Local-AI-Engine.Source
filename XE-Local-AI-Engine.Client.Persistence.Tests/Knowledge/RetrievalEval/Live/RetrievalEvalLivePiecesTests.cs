namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>The model-free pieces of the live retrieval eval: pool decorator, shuffle control, settings parsing, corpus labels, latency.</summary>
[Category(TestCategories.Unit)]
public sealed class RetrievalEvalLivePiecesTests
{
    private static readonly string[] SixDocuments = ["d0", "d1", "d2", "d3", "d4", "d5"];

    [Test]
    public async Task TopNRerankPool_SendsOnlyTheHead_AndRanksTheTailBelowItInFusionOrder()
    {
        var inner = new RecordingReranker(static documents => [.. documents.Select(static (_, index) => (double)(10 + index))]);
        var pool = new TopNRerankPool(inner, poolSize: 3);

        var scores = AssertEx.NotNull(await pool.RerankAsync("m", "q", SixDocuments, CancellationToken.None));

        AssertEx.True(inner.Received.Single().SequenceEqual(["d0", "d1", "d2"]), "only the top-N documents may reach the inner reranker.");
        AssertEx.True(scores.Take(3).SequenceEqual([10d, 11d, 12d]), "head scores pass through unchanged.");
        var ordered = scores.Select(static (score, index) => (score, index)).OrderByDescending(static pair => pair.score).Select(static pair => pair.index).ToList();
        AssertEx.True(ordered.SequenceEqual([2, 1, 0, 3, 4, 5]), $"a stable descending sort must put the tail after the head in original order, got [{string.Join(',', ordered)}].");
    }

    [Test]
    public async Task TopNRerankPool_PoolNoLargerThanN_IsAPassthrough()
    {
        var inner = new RecordingReranker(static documents => [.. documents.Select(static _ => 1d)]);
        var pool = new TopNRerankPool(inner, poolSize: 20);

        var scores = await pool.RerankAsync("m", "q", SixDocuments, CancellationToken.None);

        AssertEx.True(inner.Received.Single().SequenceEqual(SixDocuments));
        AssertEx.Equal(6, AssertEx.NotNull(scores).Count);
    }

    [Test]
    public async Task TopNRerankPool_InnerDegrade_PropagatesNull()
    {
        var pool = new TopNRerankPool(new RecordingReranker(static _ => null), poolSize: 3);

        AssertEx.Null(await pool.RerankAsync("m", "q", SixDocuments, CancellationToken.None));
    }

    [Test]
    public async Task ShuffledScoresReranker_ReturnsASeededPermutationOfTheInnerScores()
    {
        var inner = new RecordingReranker(static documents => [.. documents.Select(static (_, index) => (double)index)]);

        var first = AssertEx.NotNull(await new ShuffledScoresReranker(inner, seed: 7).RerankAsync("m", "q", SixDocuments, CancellationToken.None));
        var second = AssertEx.NotNull(await new ShuffledScoresReranker(inner, seed: 7).RerankAsync("m", "q", SixDocuments, CancellationToken.None));

        AssertEx.True(first.Order().SequenceEqual([0d, 1d, 2d, 3d, 4d, 5d]), "the shuffle must keep every inner score.");
        AssertEx.False(first.SequenceEqual([0d, 1d, 2d, 3d, 4d, 5d]), "seed 7 must actually move scores, or the control is inert.");
        AssertEx.True(first.SequenceEqual(second), "the same seed must give the same permutation.");
    }

    [Test]
    public async Task TimingReranker_RecordsScoredDegradedAndBudgetCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var timing = new TimingReranker(new RecordingReranker(static documents => documents.Count == 1 ? null : [1d, 2d]));

        _ = await timing.RerankAsync("m", "q", ["a", "b"], CancellationToken.None);
        _ = await timing.RerankAsync("m", "q", ["a"], CancellationToken.None);
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => timing.RerankAsync("m", "q", ["a", "b"], cancelled.Token));

        AssertEx.True(timing.Calls.Select(static call => call.Outcome).SequenceEqual([RerankOutcome.Scored, RerankOutcome.Degraded, RerankOutcome.BudgetCancelled]));
    }

    [Test]
    public async Task RecordingSearchService_RerankOutcomeOf_IsTheSearchesOwnCall_OrNotRerankedWhenTheGateSkippedIt()
    {
        var timing = new TimingReranker(new RecordingReranker(static documents => [.. documents.Select(static _ => 1d)]));
        var inner = Substitute.For<IKnowledgeSearchService>();
        _ = inner.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            // q0: the gate skips the rerank; q1: the budget cancels it (the service falls back); q2: it scores.
            var query = call.Arg<KnowledgeSearchRequest>().Query;
            using var budget = new CancellationTokenSource();
            if (query == "q1")
            {
                await budget.CancelAsync();
            }

            if (query != "q0")
            {
                try
                {
                    _ = await timing.RerankAsync("m", query, ["a", "b"], budget.Token);
                }
                catch (OperationCanceledException)
                {
                    // The production service degrades to fusion order here.
                }
            }

            return new KnowledgeSearchResult { Results = [] };
        });
        var recording = new RecordingSearchService(inner, timing);

        foreach (var query in new[] { "q0", "q1", "q2" })
        {
            _ = await recording.SearchAsync(new KnowledgeSearchRequest { Query = query, Limit = 5 }, CancellationToken.None);
        }

        await timing.DrainAsync();
        AssertEx.Equal(RecordingSearchService.NotReranked, recording.RerankOutcomeOf(0));
        AssertEx.Equal(nameof(RerankOutcome.BudgetCancelled), recording.RerankOutcomeOf(1));
        AssertEx.Equal(nameof(RerankOutcome.Scored), recording.RerankOutcomeOf(2));
    }

    [Test]
    public void DegradeCapturingLogger_RecordsReasonAndDocumentCount_FromTheStructuredFields()
    {
        var logger = new DegradeCapturingLogger();

        logger.LogWarning("Knowledge reranking degraded to fusion order. Reason: {Reason}. Documents: {DocumentCount}.", "timeout", 20);
        logger.LogWarning("Unrelated warning {Value}.", 3);

        AssertEx.True(logger.Degrades.SequenceEqual([("timeout", 20)]));
    }

    [Test]
    public void ParseRerankers_ReadsIdPathPairs_InOrder()
    {
        var parsed = RetrievalEvalLiveSettings.ParseRerankers(" bge=/x.gguf ; qwen3=/m@2/y.gguf;jina=/z.gguf@en;multi=/w.gguf@en,de");

        AssertEx.True(parsed.SequenceEqual([
            new LiveRerankerModel { Id = "bge", ModelPath = "/x.gguf" },
            new LiveRerankerModel { Id = "qwen3", ModelPath = "/m@2/y.gguf" },
            new LiveRerankerModel { Id = "jina", ModelPath = "/z.gguf", Languages = "en" },
            new LiveRerankerModel { Id = "multi", ModelPath = "/w.gguf", Languages = "en,de" }
        ]));
        AssertEx.Empty(RetrievalEvalLiveSettings.ParseRerankers(null));
    }

    [Test]
    [Arguments(null, "de", true)]
    [Arguments("en", "en", true)]
    [Arguments("en", "de", false)]
    [Arguments("en,de", "DE", true)]
    public void LiveRerankerModel_Supports_EveryLanguageWithoutASuffix_ElseOnlyTheDeclaredOnes(string? languages, string language, bool expected) =>
        AssertEx.Equal(expected, new LiveRerankerModel { Id = "r", ModelPath = "/r.gguf", Languages = languages }.Supports(language));

    [Test]
    [Arguments("bge")]
    [Arguments("=/x.gguf")]
    [Arguments("bge=")]
    [Arguments("bge=/x.gguf;BGE=/y.gguf")]
    [Arguments("bge=/x.gguf@")]
    [Arguments("bge=@en")]
    [Arguments("NEG-candidate=/x.gguf")]
    [Arguments("prod=/x.gguf")]
    [Arguments("F0x=/x.gguf")]
    public void ParseRerankers_MalformedDuplicateOrReserved_Throws(string raw) =>
        _ = AssertEx.Throws<FormatException>(() => RetrievalEvalLiveSettings.ParseRerankers(raw));

    [Test]
    public void FromEnvironment_WithoutBothGateVariables_IsNull_AndDefaultsApplyOtherwise()
    {
        var gated = new Dictionary<string, string?>
        {
            [RetrievalEvalLiveSettings.ServerVariable] = "/bin/llama-server"
        };
        AssertEx.Null(RetrievalEvalLiveSettings.FromEnvironment(name => gated.GetValueOrDefault(name)));

        gated[RetrievalEvalLiveSettings.EmbedModelVariable] = "/m/embed.gguf";
        var settings = AssertEx.NotNull(RetrievalEvalLiveSettings.FromEnvironment(name => gated.GetValueOrDefault(name)));
        AssertEx.Equal(RetrievalEvalLiveSettings.DefaultGpuLayers, settings.GpuLayers);
        AssertEx.Equal(RetrievalEvalLiveSettings.DefaultK, settings.K);
        AssertEx.Empty(settings.Rerankers);
        AssertEx.Null(settings.ChatModelPath);

        gated[RetrievalEvalLiveSettings.GpuLayersVariable] = "lots";
        _ = AssertEx.Throws<FormatException>(() => RetrievalEvalLiveSettings.FromEnvironment(name => gated.GetValueOrDefault(name)));
    }

    [Test]
    [Arguments("en", "en-prose", "en/a.md", true)]
    [Arguments("en", "cross-language", "en/a.md", false)]
    [Arguments("en", "multi-source", "de/b.md", false)]
    [Arguments("de", "de-prose", "de/b.md", false)]
    public void IsEnglishOnly_RequiresEnglishQueryAndNoGermanRelevantDocument(string language, string category, string relevantKey, bool expected) =>
        AssertEx.Equal(expected, LiveCorpusView.IsEnglishOnly(language, category, [relevantKey]));

    [Test]
    [Arguments("code/FtsSearch.cs.txt", "code/FtsSearch.cs")]
    [Arguments("en/notes.txt", "en/notes.txt")]
    [Arguments("de/handbuch.md", "de/handbuch.md")]
    public void SourcePathOf_UndoesOnlyThePinnedCopySuffix(string key, string expected) =>
        AssertEx.Equal(expected, LiveCorpusView.SourcePathOf(key));

    [Test]
    public void LiveLatency_UsesNearestRank()
    {
        var latency = LiveLatency.From([5d, 1d, 3d, 2d, 4d]);

        AssertEx.Equal(new LiveLatency { Count = 5, P50 = 3d, P95 = 5d, Max = 5d }, latency);
    }

    private sealed class RecordingReranker : IRerankerClient
    {
        private readonly Func<IReadOnlyList<string>, IReadOnlyList<double>?> _score;

        public RecordingReranker(Func<IReadOnlyList<string>, IReadOnlyList<double>?> score)
        {
            ArgumentNullException.ThrowIfNull(score);
            _score = score;
        }

        public List<IReadOnlyList<string>> Received { get; } = [];

        public Task<IReadOnlyList<double>?> RerankAsync(string modelName, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received.Add(documents);
            return Task.FromResult(_score(documents));
        }
    }
}
