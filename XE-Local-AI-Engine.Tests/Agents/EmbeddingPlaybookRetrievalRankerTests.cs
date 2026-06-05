namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class EmbeddingPlaybookRetrievalRankerTests
{
    private const string EmbeddingModel = "test-embed";

    [Test]
    public async Task SelectTopK_RanksByCosineSimilarity()
    {
        var weather = Action("weather forecast rain", priority: 10, createdAtUtc: 1);
        var cooking = Action("cooking recipe oven", priority: 10, createdAtUtc: 2);
        var partial = Action("forecast the weekend weather", priority: 10, createdAtUtc: 3);
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        var result = await ranker.SelectTopKAsync("weather forecast", [cooking, partial, weather], 3, CancellationToken.None);

        AssertEx.Equal(3, result.Count);
        // The two candidates that share tokens with the query embed closer than the unrelated cooking action.
        AssertEx.Equal(cooking.Id, result[2].Id, "Zero-overlap candidate ranks last by cosine.");
        AssertEx.Contains(new[]
        {
            result[0].Id,
            result[1].Id
        }, weather.Id, "Token-overlapping candidates rank ahead of the unrelated one.");
        AssertEx.Contains(new[]
        {
            result[0].Id,
            result[1].Id
        }, partial.Id, "Token-overlapping candidates rank ahead of the unrelated one.");
    }

    [Test]
    public async Task SelectTopK_OnScoreTie_BreaksByPriorityThenCreatedAtUtc()
    {
        // Identical candidate text => identical embeddings => identical cosine, so the tiebreak alone decides order.
        var lowPriority = Action("deploy", priority: 5, createdAtUtc: 99);
        var highPriorityOlder = Action("deploy", priority: 50, createdAtUtc: 1);
        var highPriorityNewer = Action("deploy", priority: 50, createdAtUtc: 2);
        var ranker = BuildRanker(new FakeEmbeddingProvider(), EmbeddingModel);

        var result = await ranker.SelectTopKAsync("deploy", [highPriorityNewer, highPriorityOlder, lowPriority], 3, CancellationToken.None);

        AssertEx.Equal(lowPriority.Id, result[0].Id, "Lower Priority wins the tiebreak.");
        AssertEx.Equal(highPriorityOlder.Id, result[1].Id, "Equal Priority breaks by older CreatedAtUtc.");
        AssertEx.Equal(highPriorityNewer.Id, result[2].Id, "Newer CreatedAtUtc sorts last on a score/Priority tie.");
    }

    [Test]
    public async Task SelectTopK_CachesCandidateVectors_ButReEmbedsQueryEachSend()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        await ranker.SelectTopKAsync("deploy production", candidates, 2, CancellationToken.None);
        var afterFirst = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("deploy production", candidates, 2, CancellationToken.None);
        var afterSecond = provider.TotalEmbeddedTexts;

        AssertEx.Equal(candidates.Count + 1, afterFirst, "First send embeds every candidate plus the query.");
        AssertEx.Equal(afterFirst + 1, afterSecond, "Second send re-uses cached candidate vectors and only re-embeds the query.");
    }

    [Test]
    public async Task SelectTopK_ReEmbedsCandidate_WhenVersionBumps()
    {
        var original = Action("deploy production build", priority: 10, createdAtUtc: 1);
        var bumped = original with
        {
            Version = original.Version + 1
        };
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        await ranker.SelectTopKAsync("deploy", [original], 1, CancellationToken.None);
        var afterOriginal = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("deploy", [bumped], 1, CancellationToken.None);
        var afterBumped = provider.TotalEmbeddedTexts;

        // Each send: 1 candidate + 1 query. The version bump invalidates the cache so the candidate is re-embedded.
        AssertEx.Equal(2, afterOriginal, "First send embeds the candidate and the query.");
        AssertEx.Equal(4, afterBumped, "A Version bump re-embeds the candidate rather than serving the cached vector.");
    }

    [Test]
    public async Task SelectTopK_EvictsOldestEntries_WhenCacheBoundExceeded()
    {
        // Bound of 1: each new candidate evicts the previous one, so a re-query of the first candidate re-embeds it.
        var first = Action("alpha task", priority: 10, createdAtUtc: 1);
        var second = Action("beta task", priority: 10, createdAtUtc: 2);
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel, cacheMaxEntries: 1);

        await ranker.SelectTopKAsync("alpha", [first], 1, CancellationToken.None); // caches first (count 1)
        await ranker.SelectTopKAsync("beta", [second], 1, CancellationToken.None); // caches second, evicts first
        var beforeReQuery = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("alpha", [first], 1, CancellationToken.None); // first evicted => re-embed
        var afterReQuery = provider.TotalEmbeddedTexts;

        AssertEx.Equal(beforeReQuery + 2, afterReQuery, "An evicted candidate is re-embedded (candidate + query) on re-query.");
    }

    [Test]
    public async Task SelectTopK_WhenEmbeddingModelBlank_DelegatesToLexical_WithoutConstructingGenerator()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, embeddingModel: null);

        var result = await ranker.SelectTopKAsync("production deploy", candidates, 2, CancellationToken.None);

        AssertEx.Equal(2, result.Count);
        AssertEx.Equal(0, provider.CreateGeneratorCallCount, "Disabled gate must never construct an embedding generator.");
    }

    [Test]
    public async Task SelectTopK_WhenGeneratorThrows_FallsBackToLexical()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider
        {
            ThrowOnGenerate = true
        };
        var ranker = BuildRanker(provider, EmbeddingModel);

        var result = await ranker.SelectTopKAsync("production deploy", candidates, 2, CancellationToken.None);

        // Falls back to the deterministic lexical ranker rather than throwing — a send never breaks on an embedding hiccup.
        AssertEx.Equal(2, result.Count);
        AssertEx.True(provider.CreateGeneratorCallCount > 0, "The active path attempts to embed before falling back.");
    }

    [Test]
    public async Task SelectTopK_WhenGeneratorReturnsFewerEmbeddingsThanInputs_FallsBackToLexical()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider
        {
            ReturnShortResponse = true
        };
        var ranker = BuildRanker(provider, EmbeddingModel);

        // A short/partial embedding response must degrade to lexical rather than throwing ArgumentOutOfRangeException
        // out of the positional indexing — the send never breaks.
        var result = await ranker.SelectTopKAsync("production deploy", candidates, 2, CancellationToken.None);

        var expected = await new LexicalPlaybookRetrievalRanker().SelectTopKAsync("production deploy", candidates, 2, CancellationToken.None);
        AssertEx.Equal(expected.Count, result.Count, "A short embedding response falls back to the lexical ranker.");
        for (var index = 0; index < expected.Count; index++)
        {
            AssertEx.Equal(expected[index].Id, result[index].Id, "Fallback yields the deterministic lexical order.");
        }
    }

    [Test]
    public async Task SelectTopK_WhenCancelled_RethrowsAndDoesNotFallBack()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider
        {
            ThrowCancellation = true
        };
        var ranker = BuildRanker(provider, EmbeddingModel);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await ranker.SelectTopKAsync("deploy", candidates, 2, cts.Token));
    }

    [Test]
    public async Task SelectTopK_WhenKIsNonPositive_ReturnsEmpty_WithoutEmbedding()
    {
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        var result = await ranker.SelectTopKAsync("deploy", SampleCandidates(), 0, CancellationToken.None);

        AssertEx.Equal(0, result.Count);
        AssertEx.Equal(0, provider.CreateGeneratorCallCount, "A non-positive k short-circuits before any embedding.");
    }

    private static EmbeddingPlaybookRetrievalRanker BuildRanker(FakeEmbeddingProvider provider,
        string? embeddingModel,
        int cacheMaxEntries = 512)
    {
        var options = Options.Create(new PlaybookRetrievalOptions
        {
            EmbeddingModelName = embeddingModel,
            EmbeddingProviderName = FakeEmbeddingProvider.ProviderKey,
            EmbeddingCacheMaxEntries = cacheMaxEntries
        });
        return new EmbeddingPlaybookRetrievalRanker(provider,
            options,
            new LexicalPlaybookRetrievalRanker(),
            NullLogger<EmbeddingPlaybookRetrievalRanker>.Instance);
    }

    private static IReadOnlyList<PlaybookActionRecord> SampleCandidates()
    {
        return
        [
            Action("deploy the production build to the cluster", priority: 10, createdAtUtc: 1),
            Action("summarise the meeting notes", priority: 20, createdAtUtc: 2),
            Action("production incident response runbook", priority: 30, createdAtUtc: 3)
        ];
    }

    private static PlaybookActionRecord Action(string triggerCondition, int priority, long createdAtUtc)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            triggerCondition,
            Behavior: "behaviour text",
            Scope: null,
            priority,
            Version: 1,
            createdAtUtc,
            UpdatedAtUtc: createdAtUtc);
    }

    // A fake node-local provider whose embedding generator derives deterministic vectors from token hashing, so texts
    // that share more tokens score a higher cosine. It records generator construction and the total number of texts
    // embedded so the cache-hit / re-embed assertions can observe the round-trips.
    private sealed class FakeEmbeddingProvider : ILocalModelProvider
    {
        public const string ProviderKey = "fake";

        public int CreateGeneratorCallCount { get; private set; }

        public int TotalEmbeddedTexts { get; private set; }

        public bool ThrowOnGenerate { get; set; }

        public bool ThrowCancellation { get; set; }

        public bool ReturnShortResponse { get; set; }

        public string ProviderName => ProviderKey;

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            CreateGeneratorCallCount++;
            return new FakeEmbeddingGenerator(this);
        }

        public IChatClient CreateChatClient(LocalModelSelection selection)
        {
            throw new NotSupportedException();
        }

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task WarmModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task UnloadModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        private sealed class FakeEmbeddingGenerator(FakeEmbeddingProvider owner) : IEmbeddingGenerator<string, Embedding<float>>
        {
            private const int Dimensions = 16;

            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (owner.ThrowCancellation)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (owner.ThrowOnGenerate)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                var embeddings = new List<Embedding<float>>();
                foreach (var value in values)
                {
                    owner.TotalEmbeddedTexts++;
                    embeddings.Add(new Embedding<float>(BuildVector(value)));
                }

                if (owner.ReturnShortResponse && embeddings.Count > 0)
                {
                    // Simulate a misbehaving/partial response that returns fewer embeddings than inputs.
                    embeddings.RemoveAt(embeddings.Count - 1);
                }

                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }

            // A bag-of-tokens vector: each token contributes to a fixed bucket, so texts sharing tokens point in a
            // similar direction (higher cosine) and unrelated texts are near-orthogonal — deterministic and Ollama-free.
            private static ReadOnlyMemory<float> BuildVector(string text)
            {
                var vector = new float[Dimensions];
                foreach (var token in text.ToUpperInvariant()
                                          .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var bucket = (int)((uint)StringComparer.Ordinal.GetHashCode(token) % Dimensions);
                    vector[bucket] += 1f;
                }

                return vector;
            }
        }
    }
}
