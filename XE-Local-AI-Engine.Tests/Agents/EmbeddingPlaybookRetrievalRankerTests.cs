namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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

        var result = await ranker.SelectTopKAsync("weather forecast", [cooking, partial, weather], k: 3, CancellationToken.None);

        AssertEx.Equal(expected: 3, result.Count);
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

        var result = await ranker.SelectTopKAsync("deploy", [highPriorityNewer, highPriorityOlder, lowPriority], k: 3, CancellationToken.None);

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

        await ranker.SelectTopKAsync("deploy production", candidates, k: 2, CancellationToken.None);
        var afterFirst = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("deploy production", candidates, k: 2, CancellationToken.None);
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

        await ranker.SelectTopKAsync("deploy", [original], k: 1, CancellationToken.None);
        var afterOriginal = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("deploy", [bumped], k: 1, CancellationToken.None);
        var afterBumped = provider.TotalEmbeddedTexts;

        // Each send: 1 candidate + 1 query. The version bump invalidates the cache so the candidate is re-embedded.
        AssertEx.Equal(expected: 2, afterOriginal, "First send embeds the candidate and the query.");
        AssertEx.Equal(expected: 4, afterBumped, "A Version bump re-embeds the candidate rather than serving the cached vector.");
    }

    [Test]
    public async Task SelectTopK_EvictsOldestEntries_WhenCacheBoundExceeded()
    {
        // Bound of 1: each new candidate evicts the previous one, so a re-query of the first candidate re-embeds it.
        var first = Action("alpha task", priority: 10, createdAtUtc: 1);
        var second = Action("beta task", priority: 10, createdAtUtc: 2);
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel, cacheMaxEntries: 1);

        await ranker.SelectTopKAsync("alpha", [first], k: 1, CancellationToken.None); // caches first (count 1)
        await ranker.SelectTopKAsync("beta", [second], k: 1, CancellationToken.None); // caches second, evicts first
        var beforeReQuery = provider.TotalEmbeddedTexts;
        await ranker.SelectTopKAsync("alpha", [first], k: 1, CancellationToken.None); // first evicted => re-embed
        var afterReQuery = provider.TotalEmbeddedTexts;

        AssertEx.Equal(beforeReQuery + 2, afterReQuery, "An evicted candidate is re-embedded (candidate + query) on re-query.");
    }

    [Test]
    public async Task SelectTopK_WhenEmbeddingModelBlank_DelegatesToLexical_WithoutConstructingGenerator()
    {
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, embeddingModel: null);

        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "Disabled gate must never construct an embedding generator.");
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

        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        // Falls back to the deterministic lexical ranker rather than throwing — a send never breaks on an embedding hiccup.
        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.True(provider.CreateGeneratorCallCount > 0, "The active path attempts to embed before falling back.");
    }

    [Test]
    public async Task SelectTopK_WhenTheOperatorEjectedTheEmbedder_FallsBackToLexicalWithoutAWarning()
    {
        // A Background load of an ejected embedder is vetoed with this type, unwrapped: an expected state, not a failure.
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider
        {
            GenerateFailure = new OperatorEjectedException("The embedding model was ejected by the operator.")
        };
        var logger = new RecordingLogger<EmbeddingPlaybookRetrievalRanker>();
        var ranker = BuildRanker(provider, EmbeddingModel, logger: logger);

        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        var expected = await new LexicalPlaybookRetrievalRanker().SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);
        AssertEx.True(result.Select(static action => action.Id).SequenceEqual(expected.Select(static action => action.Id)),
            "An operator eject yields the deterministic lexical order.");
        AssertEx.False(logger.Entries.Any(static entry => entry.Level >= LogLevel.Warning), "An eject veto logs no Warning.");
        var line = logger.Entries.Single(static entry => entry.Level == LogLevel.Information);
        AssertEx.Contains(line.Message, "operator-ejected, ranking lexically");
        AssertEx.Contains(line.Message, EmbeddingModel);
        AssertEx.Null(line.Exception, "The veto is logged without the exception object.");
    }

    [Test]
    public async Task SelectTopK_WhenTheGeneratorFailsForAnotherReason_StillWarnsWithTheException()
    {
        var provider = new FakeEmbeddingProvider
        {
            ThrowOnGenerate = true
        };
        var logger = new RecordingLogger<EmbeddingPlaybookRetrievalRanker>();
        var ranker = BuildRanker(provider, EmbeddingModel, logger: logger);

        _ = await ranker.SelectTopKAsync("production deploy", SampleCandidates(), k: 2, CancellationToken.None);

        var warning = logger.Entries.Single(static entry => entry.Level == LogLevel.Warning);
        AssertEx.True(warning.Exception is HttpRequestException, "Any other failure keeps its Warning with the exception.");
    }

    [Test]
    public async Task SelectTopK_InsideAnUnattendedRun_EmbedsAsBackground_WhileAnOverlappingSendStaysInteractive()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeEmbeddingProvider
        {
            GenerateGate = release.Task
        };
        var ranker = BuildRanker(provider, EmbeddingModel);
        var candidates = SampleCandidates();

        Task<IReadOnlyList<PlaybookActionRecord>> unattended;
        using (UnattendedRunScope.Begin())
        {
            unattended = ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);
        }

        // Starts while the unattended ranking is still parked inside its embedding call.
        var interactive = ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);
        var intents = provider.Intents;
        release.SetResult();
        _ = await unattended;
        _ = await interactive;

        AssertEx.True(intents.SequenceEqual([ModelResidencyIntent.Background, ModelResidencyIntent.Interactive]),
            $"The unattended run embeds as Background and the overlapping send as Interactive, got [{string.Join(", ", intents)}].");
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
        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        var expected = await new LexicalPlaybookRetrievalRanker().SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);
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

        await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await ranker.SelectTopKAsync("deploy", candidates, k: 2, cts.Token));
    }

    [Test]
    public async Task SelectTopK_WhenKIsNonPositive_ReturnsEmpty_WithoutEmbedding()
    {
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        var result = await ranker.SelectTopKAsync("deploy", SampleCandidates(), k: 0, CancellationToken.None);

        AssertEx.Equal(expected: 0, result.Count);
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "A non-positive k short-circuits before any embedding.");
    }

    [Test]
    public async Task ModelRoutingEmbeddings_RoutesByModelId_ToPoolingProcess()
    {
        // EmbeddingProviderName routes the embedding model to its provider; the resolved provider's generator embeds
        // the batch (candidate(s) + query). This is the resolver-routed equivalent of the llama-server embedding
        // (pooling) process — here a fake stands in so no llama-server is needed.
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel);

        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.True(provider.CreateGeneratorCallCount > 0, "The configured embedding provider was routed to and embedded the batch.");
        AssertEx.Equal(candidates.Count + 1, provider.TotalEmbeddedTexts, "Routing embeds every candidate plus the query in one batch.");
    }

    [Test]
    public async Task SelectTopK_WhenEmbeddingProviderUnregistered_FallsBackToLexical()
    {
        // A misconfigured EmbeddingProviderName the resolver can't resolve must degrade to lexical (the resolver throws
        // InvalidOperationException, now in the ranker's caught-degrade set), never breaking the send.
        var candidates = SampleCandidates();
        var provider = new FakeEmbeddingProvider();
        var ranker = BuildRanker(provider, EmbeddingModel, embeddingProviderName: "not-registered");

        var result = await ranker.SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);

        var expected = await new LexicalPlaybookRetrievalRanker().SelectTopKAsync("production deploy", candidates, k: 2, CancellationToken.None);
        AssertEx.Equal(expected.Count, result.Count, "An unregistered embedding provider degrades to lexical.");
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "An unresolved provider never reaches the fake generator.");
    }

    private static EmbeddingPlaybookRetrievalRanker BuildRanker(FakeEmbeddingProvider provider,
        string? embeddingModel,
        int cacheMaxEntries = 512,
        string embeddingProviderName = FakeEmbeddingProvider.ProviderKey,
        ILogger<EmbeddingPlaybookRetrievalRanker>? logger = null)
    {
        var options = Options.Create(new PlaybookRetrievalOptions
        {
            EmbeddingModelName = embeddingModel,
            EmbeddingProviderName = embeddingProviderName,
            EmbeddingCacheMaxEntries = cacheMaxEntries
        });
        // The ranker now resolves its embedding provider by EmbeddingProviderName via the resolver; wrap the fake as the
        // single registered + default provider so routing resolves it (and an unregistered name degrades to lexical).
        var resolver = SingleProviderResolverFactory.Create(provider);
        return new EmbeddingPlaybookRetrievalRanker(resolver,
            options,
            new LexicalPlaybookRetrievalRanker(),
            logger ?? NullLogger<EmbeddingPlaybookRetrievalRanker>.Instance,
            TimeProvider.System);
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
        return new PlaybookActionRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            State = PlaybookActionState.Enabled,
            Source = PlaybookActionSource.Manual,
            TriggerCondition = triggerCondition,
            Behavior = "behaviour text",
            Scope = null,
            Priority = priority,
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    // A fake node-local provider whose embedding generator derives deterministic vectors from token hashing, so texts
    // that share more tokens score a higher cosine. It records generator construction and the total number of texts
    // embedded so the cache-hit / re-embed assertions can observe the round-trips.
    private sealed class FakeEmbeddingProvider : ILocalModelProvider
    {
        public const string ProviderKey = "fake";

        private readonly List<ModelResidencyIntent> _intents = [];
        private readonly Lock _intentsGate = new();

        public int CreateGeneratorCallCount { get; private set; }

        public int TotalEmbeddedTexts { get; private set; }

        public bool ThrowOnGenerate { get; set; }

        public bool ThrowCancellation { get; set; }

        public bool ReturnShortResponse { get; set; }

        /// <summary>Thrown by every embedding call when set.</summary>
        public Exception? GenerateFailure { get; init; }

        /// <summary>When set, every embedding call waits for it first, so a test can overlap two rankings.</summary>
        public Task? GenerateGate { get; init; }

        /// <summary>The residency intent of every generator created, in creation order.</summary>
        public IReadOnlyList<ModelResidencyIntent> Intents
        {
            get
            {
                lock (_intentsGate)
                {
                    return _intents.ToArray();
                }
            }
        }

        public string ProviderName => ProviderKey;

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            CreateGeneratorCallCount++;
            lock (_intentsGate)
            {
                _intents.Add(selection.ResidencyIntent);
            }

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

        private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            private const int Dimensions = 16;
            private readonly FakeEmbeddingProvider _owner;

            public FakeEmbeddingGenerator(FakeEmbeddingProvider owner)
            {
                _owner = owner;
            }

            public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (_owner.GenerateGate is { } gate)
                {
                    await gate;
                }

                if (_owner.ThrowCancellation)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (_owner.ThrowOnGenerate)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                if (_owner.GenerateFailure is not null)
                {
                    throw _owner.GenerateFailure;
                }

                var embeddings = new List<Embedding<float>>();
                foreach (var value in values)
                {
                    _owner.TotalEmbeddedTexts++;
                    embeddings.Add(new Embedding<float>(BuildVector(value)));
                }

                if (_owner.ReturnShortResponse && embeddings.Count > 0)
                {
                    // Simulate a misbehaving/partial response that returns fewer embeddings than inputs.
                    embeddings.RemoveAt(embeddings.Count - 1);
                }

                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }

            // A bag-of-tokens vector: each token contributes to a fixed bucket, so texts sharing tokens point in a
            // similar direction (higher cosine) and unrelated texts are near-orthogonal — deterministic and runtime-free.
            // The bucket derives from a stable FNV-1a hash rather than string.GetHashCode/StringComparer.GetHashCode:
            // those are randomized per process, so token->bucket bucketing (and thus the cosine ranking) would change
            // each run and could collide an unrelated token into a query bucket, flaking SelectTopK_RanksByCosineSimilarity.
            // FNV-1a is process-independent, so the ordering these tests assert is identical on every run.
            private static ReadOnlyMemory<float> BuildVector(string text)
            {
                var vector = new float[Dimensions];
                foreach (var token in text.ToUpperInvariant()
                                          .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var bucket = (int)(StableHash(token) % Dimensions);
                    vector[bucket] += 1f;
                }

                return vector;
            }

            // Deterministic 32-bit FNV-1a over the token's chars — stable across processes (unlike string.GetHashCode).
            private static uint StableHash(string token)
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;
                var hash = offsetBasis;
                foreach (var c in token)
                {
                    hash ^= c;
                    hash *= prime;
                }

                return hash;
            }
        }
    }
}
