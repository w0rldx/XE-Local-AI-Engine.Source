namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node's embedding-backed tool-relevance selector. Two properties carry the design. First, it is a strict
///     OPTIMISATION over the lexical selector: every failure shape — no model configured, a dead runtime, a partial
///     response, an unregistered provider, and above all its own timeout — degrades to the lexical selection rather
///     than throwing, because this one runs inside the send, in front of the first token. Second, nothing it logs
///     carries a tool name, a description or the user's query.
/// </summary>
public sealed class EmbeddingToolRelevanceSelectorTests
{
    private const string EmbeddingModel = "test-embed";
    private const string Query = "deploy the production build";
    private const int MinimumRankedSlots = 2;
    private const int Threshold = 6;

    [Test]
    public async Task SelectAsync_WithNoEmbeddingModelConfigured_DelegatesToLexicalWithoutConstructingAGenerator()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider, embeddingModel: null);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        var expected = await Lexical().SelectAsync(Query, candidates, Threshold, CancellationToken.None);
        AssertEx.True(selection.OfferedNames.SequenceEqual(expected.OfferedNames, StringComparer.Ordinal),
            "The unset default is the lexical selection, exactly.");
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "The disabled gate must never construct an embedding generator.");
    }

    [Test]
    public async Task SelectAsync_AtOrBelowTheThreshold_TakesTheFastPathWithoutEmbedding()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, candidates.Count, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count, "At the threshold the whole array is offered.");
        AssertEx.Equal(expected: 0, selection.HiddenNames.Count);
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "The fast path never reaches a model.");
    }

    [Test]
    public async Task SelectAsync_AboveTheThreshold_RanksByCosineAndHidesTheLeastRelevant()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        AssertEx.True(provider.CreateGeneratorCallCount > 0, "The active path embeds.");
        AssertEx.True(selection.HiddenNames.Count > 0, "Above the threshold with more rankable tools than slots, something is held back.");
        AssertEx.Contains(selection.OfferedNames, "deploy_production_build", "The tool the query names embeds closest to it.");
        AssertEx.Contains(selection.OfferedNames, "core_tool", "Core is never ranked and never trimmed.");
    }

    [Test]
    public async Task SelectAsync_NeverEmbedsACoreTool()
    {
        // Core is offered whatever it scores, so embedding it would buy nothing and cost a longer batch.
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();

        _ = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        AssertEx.False(provider.EmbeddedTexts.Any(static text => text.Contains("core_tool", StringComparison.Ordinal)),
            "The batch carries only the candidates a ranking can move.");
    }

    [Test]
    public async Task SelectAsync_CachesToolVectors_ButReEmbedsTheQueryEveryTurn()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();
        var rankable = candidates.Count(static candidate => !candidate.IsCore);

        _ = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);
        var afterFirst = provider.TotalEmbeddedTexts;
        _ = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);
        var afterSecond = provider.TotalEmbeddedTexts;

        AssertEx.Equal(rankable + 1, afterFirst, "The first turn embeds every rankable tool plus the query, in one batch.");
        AssertEx.Equal(afterFirst + 1, afterSecond, "The second turn reuses the cached tool vectors and re-embeds only the query.");
    }

    [Test]
    public async Task SelectAsync_WhenAToolDescriptionChanges_ReEmbedsThatTool()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();
        var edited = candidates.Select(static candidate => candidate.Name == "deploy_production_build"
                                   ? candidate with
                                   {
                                       Description = "an entirely different description"
                                   }
                                   : candidate)
                               .ToList();

        _ = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);
        var afterFirst = provider.TotalEmbeddedTexts;
        _ = await selector.SelectAsync(Query, edited, Threshold, CancellationToken.None);
        var afterEdit = provider.TotalEmbeddedTexts;

        AssertEx.Equal(afterFirst + 2, afterEdit, "An edited description is a different cache key: that one tool plus the query are re-embedded.");
    }

    [Test]
    public async Task SelectAsync_WhenTheGeneratorThrowsIOException_FallsBackToLexical()
    {
        // A profiling refusal whose bounded retry is spent, an operator eject, and a supervisor runtime failure all
        // reach a caller as IOException rather than as their own types.
        var provider = new FakeEmbeddingProvider
        {
            GenerateFailure = new IOException("llama-server refused the request while profiling")
        };
        var (selector, logger) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
        AssertEx.True(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning), "One warning marks the degrade.");
    }

    [Test]
    public async Task SelectAsync_WhenTheGeneratorThrowsHttpRequestException_FallsBackToLexical()
    {
        var provider = new FakeEmbeddingProvider
        {
            GenerateFailure = new HttpRequestException("fake embedding transport failure")
        };
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
    }

    [Test]
    public async Task SelectAsync_WhenTheEmbeddingProviderIsUnregistered_FallsBackToLexical()
    {
        var provider = new FakeEmbeddingProvider();
        var (selector, _) = BuildSelector(provider, embeddingProviderName: "not-registered");
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "An unresolved provider never reaches a generator.");
    }

    [Test]
    public async Task SelectAsync_WhenTheGeneratorReturnsFewerEmbeddingsThanInputs_FallsBackToLexical()
    {
        var provider = new FakeEmbeddingProvider
        {
            ReturnShortResponse = true
        };
        var (selector, _) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
    }

    [Test]
    public async Task SelectAsync_WhenTheEmbeddingTimeoutExpires_FallsBackToLexicalInsteadOfThrowing()
    {
        // The ⚑ of the plan: the relevance hop calls this under CancellationToken.None, so the ONLY cancellation that
        // can arrive is this selector's own EmbeddingTimeout — the thing that must degrade. Copying the playbook
        // ranker's "rethrow a cancellation" clause verbatim would fail the send instead, which is the H5 bound
        // inverted.
        var provider = new FakeEmbeddingProvider
        {
            GenerateDelay = TimeSpan.FromMinutes(5)
        };
        var (selector, logger) = BuildSelector(provider, embeddingTimeout: TimeSpan.FromMilliseconds(20));
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
        AssertEx.True(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning), "The expiry degrades and says so once.");
    }

    [Test]
    public async Task SelectAsync_WhenTheEmbeddingTimeoutExpires_DoesNotCancelTheCallersToken()
    {
        // The linked source is the selector's own; expiring it must leave the send's token untouched, or the bound
        // would take the turn down with the ranking.
        var provider = new FakeEmbeddingProvider
        {
            GenerateDelay = TimeSpan.FromMinutes(5)
        };
        var (selector, _) = BuildSelector(provider, embeddingTimeout: TimeSpan.FromMilliseconds(20));
        using var caller = new CancellationTokenSource();

        _ = await selector.SelectAsync(Query, SampleCandidates(), Threshold, caller.Token);

        AssertEx.False(caller.IsCancellationRequested, "The selector's own bound never cancels the send.");
    }

    [Test]
    public async Task SelectAsync_WhenTheCallerCancels_Propagates()
    {
        // Unreachable through the hop, which passes None, and asserted so the guard stays honest if a future revision
        // flows a real token in.
        var provider = new FakeEmbeddingProvider
        {
            GenerateDelay = TimeSpan.FromMinutes(5)
        };
        var (selector, _) = BuildSelector(provider);
        using var caller = new CancellationTokenSource();
        var selection = selector.SelectAsync(Query, SampleCandidates(), Threshold, caller.Token);

        await caller.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await selection);
    }

    [Test]
    public async Task SelectAsync_OnADegrade_LogsNoToolNameDescriptionOrQueryText()
    {
        var provider = new FakeEmbeddingProvider
        {
            GenerateFailure = new IOException("runtime unavailable")
        };
        var (selector, logger) = BuildSelector(provider);
        var candidates = SampleCandidates();

        _ = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        var logged = string.Join('\n', logger.Entries.Select(static entry => entry.Message));
        AssertEx.False(logged.Contains(Query, StringComparison.OrdinalIgnoreCase), "The user's query never reaches a log.");
        foreach (var candidate in candidates)
        {
            AssertEx.False(logged.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase), "No tool name reaches a log.");
            AssertEx.False(logged.Contains(candidate.Description ?? " ", StringComparison.OrdinalIgnoreCase), "No tool description reaches a log.");
        }
    }

    [Test]
    public async Task SelectAsync_WhenTheBatchDegradesAfterTheBoundHasExpired_LogsOneDegradeNotTwo()
    {
        // The batch comes back short (a degrade that is NOT an exception) only once the 2 s bound has already fired.
        // Handing that inner fallback the BOUNDED token would make the lexical selector's opening
        // ThrowIfCancellationRequested throw, the outer catch would degrade a second time with the right token, and one
        // degrade would log two warnings. The caller's token is the one that keeps this a single event.
        var provider = new FakeEmbeddingProvider
        {
            ReturnShortResponse = true,
            ReturnsAfterTheBoundExpires = true
        };
        var (selector, logger) = BuildSelector(provider, embeddingTimeout: TimeSpan.FromMilliseconds(20));
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        await AssertMatchesLexicalAsync(selection, candidates);
        AssertEx.Equal(expected: 1,
            logger.Entries.Count(static entry => entry.Level == LogLevel.Warning),
            "One degrade is one warning, whether or not the bound had already expired when it happened.");
    }

    [Test]
    public async Task SelectAsync_WhenACandidateVectorIsEmpty_ScoresZeroAndKeepsTheDeterministicIndexOrder()
    {
        // CosineScore's first guard. An empty vector carries no comparable signal, so every candidate ties at zero and
        // the ThenBy(Index) tiebreak decides — a stable answer rather than a throw.
        await AssertDegenerateVectorsFallToIndexOrderAsync(static _ => ReadOnlyMemory<float>.Empty);
    }

    [Test]
    public async Task SelectAsync_WhenACandidateVectorHasAMismatchedLength_ScoresZeroAndKeepsTheDeterministicIndexOrder()
    {
        // The guard that matters most: without it a mid-run dimension change reaches TensorPrimitives.CosineSimilarity
        // as an ArgumentException, which is OUTSIDE this selector's catch set — so the turn would lose the filter
        // entirely at the hop's own catch instead of degrading to lexical here.
        await AssertDegenerateVectorsFallToIndexOrderAsync(static _ => new float[]
        {
            1f,
            0f,
            0f,
            0f
        });
    }

    [Test]
    public async Task SelectAsync_WhenACandidateVectorHasZeroMagnitude_GuardsTheNaNCosineToZero()
    {
        // Same dimension, no direction: CosineSimilarity divides by a zero magnitude and returns NaN, which sorts
        // unpredictably. The guard turns it into the same honest zero the other two shapes produce.
        await AssertDegenerateVectorsFallToIndexOrderAsync(static _ => new float[16]);
    }

    [Test]
    public async Task SelectAsync_WithANullToolDescription_RanksWithoutThrowing()
    {
        // A null Description reaches three places at once — the cache key, the byte estimator and CandidateText — and
        // an AITool is free to carry none.
        var provider = new FakeEmbeddingProvider();
        var (selector, logger) = BuildSelector(provider);
        var candidates = SampleCandidates()
                         .Select(static candidate => candidate.Name == "deploy_production_build"
                             ? candidate with
                             {
                                 Description = null
                             }
                             : candidate)
                         .ToList();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        AssertEx.Equal(candidates.Count, selection.OfferedNames.Count + selection.HiddenNames.Count, "Every candidate is accounted for.");
        AssertEx.Contains(selection.OfferedNames, "core_tool", "Core is never ranked and never trimmed.");
        AssertEx.False(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning), "A description-free tool is ranked, not a degrade.");
    }

    // Every non-core candidate gets the same degenerate vector, so all seven tie at a zero score and the offer is
    // decided by the index tiebreak alone: core plus the first five rankable names, in array order.
    private static async Task AssertDegenerateVectorsFallToIndexOrderAsync(Func<string, ReadOnlyMemory<float>> degenerate)
    {
        var provider = new FakeEmbeddingProvider
        {
            OverrideToolVector = degenerate
        };
        var (selector, logger) = BuildSelector(provider);
        var candidates = SampleCandidates();

        var selection = await selector.SelectAsync(Query, candidates, Threshold, CancellationToken.None);

        AssertEx.True(selection.OfferedNames.SequenceEqual(["core_tool", "deploy_production_build", "summarise_notes", "incident_runbook", "weather_forecast", "cooking_recipe"],
                StringComparer.Ordinal),
            "A tie across every candidate resolves to the deterministic candidate order.");
        AssertEx.True(selection.HiddenNames.SequenceEqual(["translate_text", "play_music"], StringComparer.Ordinal));
        AssertEx.False(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning), "A degenerate vector is scored, not a degrade.");
    }

    private static async Task AssertMatchesLexicalAsync(ToolRelevanceSelection selection, IReadOnlyList<ToolRelevanceCandidate> candidates)
    {
        var expected = await Lexical().SelectAsync(Query, candidates, Threshold, CancellationToken.None);
        AssertEx.True(selection.OfferedNames.SequenceEqual(expected.OfferedNames, StringComparer.Ordinal),
            "A degrade yields exactly the deterministic lexical offer.");
        AssertEx.True(selection.HiddenNames.SequenceEqual(expected.HiddenNames, StringComparer.Ordinal),
            "A degrade yields exactly the deterministic lexical hidden set.");
    }

    // The same options the selector under test carries, so a degrade is compared against the ranking it actually
    // degrades to rather than against one with a different ranked-slot floor.
    private static LexicalToolRelevanceSelector Lexical()
    {
        return new LexicalToolRelevanceSelector(Options.Create(new ToolRelevanceOptions
        {
            MinimumRankedSlots = MinimumRankedSlots
        }));
    }

    private static (EmbeddingToolRelevanceSelector Selector, RecordingLogger<EmbeddingToolRelevanceSelector> Logger) BuildSelector(FakeEmbeddingProvider provider,
        string? embeddingModel = EmbeddingModel,
        string embeddingProviderName = FakeEmbeddingProvider.ProviderKey,
        TimeSpan? embeddingTimeout = null)
    {
        var options = Options.Create(new ToolRelevanceOptions
        {
            EmbeddingModelName = embeddingModel,
            EmbeddingProviderName = embeddingProviderName,
            EmbeddingTimeout = embeddingTimeout ?? TimeSpan.FromSeconds(2),
            MinimumRankedSlots = MinimumRankedSlots
        });
        var logger = new RecordingLogger<EmbeddingToolRelevanceSelector>();
        var selector = new EmbeddingToolRelevanceSelector(SingleProviderResolverFactory.Create(provider),
            options,
            new LexicalToolRelevanceSelector(options),
            logger);
        return (selector, logger);
    }

    // Eight candidates, one of them core, with a threshold of 6 and MinimumRankedSlots 2 — so the ranker fills two of
    // the seven rankable slots and five names are held back.
    private static IReadOnlyList<ToolRelevanceCandidate> SampleCandidates()
    {
        return
        [
            new ToolRelevanceCandidate("core_tool", "the always-on state tool", IsCore: true),
            new ToolRelevanceCandidate("deploy_production_build", "deploy the production build to the cluster", IsCore: false),
            new ToolRelevanceCandidate("summarise_notes", "summarise the meeting notes", IsCore: false),
            new ToolRelevanceCandidate("incident_runbook", "production incident response runbook", IsCore: false),
            new ToolRelevanceCandidate("weather_forecast", "the weekend weather forecast", IsCore: false),
            new ToolRelevanceCandidate("cooking_recipe", "an oven recipe", IsCore: false),
            new ToolRelevanceCandidate("translate_text", "translate a phrase", IsCore: false),
            new ToolRelevanceCandidate("play_music", "start a playlist", IsCore: false)
        ];
    }

    // A fake node-local provider whose generator derives deterministic vectors from token hashing, so texts sharing
    // tokens score a higher cosine. It records generator construction and every text embedded, so the cache-hit,
    // core-is-never-embedded and one-round-trip assertions can observe the batches. Shaped after
    // EmbeddingPlaybookRetrievalRankerTests' fake, with a delay knob for the timeout arm.
    private sealed class FakeEmbeddingProvider : ILocalModelProvider
    {
        public const string ProviderKey = "fake";

        private readonly List<string> _embeddedTexts = [];
        private readonly Lock _gate = new();

        public int CreateGeneratorCallCount { get; private set; }

        public IReadOnlyList<string> EmbeddedTexts
        {
            get
            {
                lock (_gate)
                {
                    return _embeddedTexts.ToArray();
                }
            }
        }

        public Exception? GenerateFailure { get; init; }

        public TimeSpan GenerateDelay { get; init; }

        public bool ReturnShortResponse { get; init; }

        /// <summary>Replaces the derived vector for every TOOL text; the query keeps its real one.</summary>
        public Func<string, ReadOnlyMemory<float>>? OverrideToolVector { get; init; }

        /// <summary>
        ///     Waits for the caller's token before returning, so a response shape that degrades WITHOUT throwing
        ///     (<see cref="ReturnShortResponse" />) can be made to land after the selector's own bound has expired —
        ///     deterministically, rather than by racing a delay against the timeout.
        /// </summary>
        public bool ReturnsAfterTheBoundExpires { get; init; }

        public int TotalEmbeddedTexts => EmbeddedTexts.Count;

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

        private void Record(string text)
        {
            lock (_gate)
            {
                _embeddedTexts.Add(text);
            }
        }

        private sealed class FakeEmbeddingGenerator(FakeEmbeddingProvider owner) : IEmbeddingGenerator<string, Embedding<float>>
        {
            private const int Dimensions = 16;

            public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (owner.GenerateDelay > TimeSpan.Zero)
                {
                    await Task.Delay(owner.GenerateDelay, cancellationToken);
                }

                if (owner.GenerateFailure is not null)
                {
                    throw owner.GenerateFailure;
                }

                // The batch is the missing tool texts followed by the query, so the override applies to everything but
                // the last entry: a test that degrades the CANDIDATE vectors still scores them against a real query.
                var texts = values.ToList();
                var embeddings = new List<Embedding<float>>();
                for (var position = 0; position < texts.Count; position++)
                {
                    var value = texts[position];
                    owner.Record(value);
                    var isQuery = position == texts.Count - 1;
                    embeddings.Add(new Embedding<float>(!isQuery && owner.OverrideToolVector is { } overrideVector ? overrideVector(value) : BuildVector(value)));
                }

                if (owner.ReturnShortResponse && embeddings.Count > 0)
                {
                    // A misbehaving or partial response that returns fewer embeddings than inputs.
                    embeddings.RemoveAt(embeddings.Count - 1);
                }

                if (owner.ReturnsAfterTheBoundExpires)
                {
                    var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using var registration = cancellationToken.Register(() => expired.TrySetResult());
                    await expired.Task;
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

            // A bag-of-tokens vector: each token lands in a fixed bucket, so texts sharing tokens point in a similar
            // direction and unrelated texts are near-orthogonal. The bucket derives from a process-independent FNV-1a
            // rather than string.GetHashCode, which is randomized per process and would flake the ranking assertions.
            private static ReadOnlyMemory<float> BuildVector(string text)
            {
                var vector = new float[Dimensions];
                foreach (var token in text.ToUpperInvariant()
                                          .Split([' ', '\t', '\n', '\r', '_'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var bucket = (int)(StableHash(token) % Dimensions);
                    vector[bucket] += 1f;
                }

                return vector;
            }

            private static uint StableHash(string token)
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;
                var hash = offsetBasis;
                foreach (var character in token)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return hash;
            }
        }
    }
}
