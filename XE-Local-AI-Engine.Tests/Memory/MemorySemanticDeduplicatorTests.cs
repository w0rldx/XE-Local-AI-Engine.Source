namespace XE_Local_AI_Engine.Tests.Memory;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioural tests for <see cref="MemorySemanticDeduplicator" />: a paraphrase (cosine-near an existing memory) is
///     flagged; a genuinely distinct candidate is kept; a not-<c>IsConfident</c> resolution (or an embedder failure)
///     returns NotApplied so the caller keeps every candidate (no mass-dedup on outage); the configured threshold is the
///     deciding knob; scope confines the comparison; and existing-memory vectors are cached (RAM-only) while the
///     candidate is re-embedded each run. A deterministic map-based fake embedder makes cosine fully controllable
///     (mirrors the KB/ranker test embedder pattern) — no Ollama/llama-server.
/// </summary>
public sealed class MemorySemanticDeduplicatorTests
{
    private const string ResolvedModel = "test-embed";

    [Test]
    public async Task FindSemanticDuplicates_WhenParaphraseOfExisting_FlagsCandidate()
    {
        // Existing memory and the candidate map to the SAME direction => cosine 1.0 >= threshold => the paraphrase (worded
        // differently but same meaning) is flagged even though its exact lexical key differs.
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("paraphrased helper lesson", 1f, 0f, 0f);
        var deduplicator = Build(vectors, out _);

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("paraphrased helper lesson", MemoryScope.Procedural)],
            CancellationToken.None);

        AssertEx.True(result.Applied, "A confident embedding model means semantic dedup runs.");
        AssertEx.True(result.DuplicateIndexes.Contains(0), "The paraphrase must be flagged as a semantic duplicate.");
    }

    [Test]
    public async Task FindSemanticDuplicates_WhenDistinct_KeepsCandidate()
    {
        // Orthogonal vectors => cosine 0 < threshold => the distinct lesson is kept.
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("unrelated deployment lesson", 0f, 1f, 0f);
        var deduplicator = Build(vectors, out _);

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("unrelated deployment lesson", MemoryScope.Procedural)],
            CancellationToken.None);

        AssertEx.True(result.Applied);
        AssertEx.Equal(expected: 0, result.DuplicateIndexes.Count, "A genuinely distinct candidate is never flagged.");
    }

    [Test]
    public async Task FindSemanticDuplicates_MixedBatch_FlagsOnlyTheParaphrase()
    {
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("paraphrased helper lesson", 1f, 0f, 0f)
                      .Set("unrelated deployment lesson", 0f, 1f, 0f);
        var deduplicator = Build(vectors, out _);

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [
                Candidate("paraphrased helper lesson", MemoryScope.Procedural),
                Candidate("unrelated deployment lesson", MemoryScope.Procedural)
            ],
            CancellationToken.None);

        AssertEx.True(result.DuplicateIndexes.Contains(0), "The paraphrase (index 0) is flagged.");
        AssertEx.False(result.DuplicateIndexes.Contains(1), "The distinct candidate (index 1) is kept.");
    }

    [Test]
    public async Task FindSemanticDuplicates_WhenNotConfident_ReturnsNotApplied_AndNeverEmbeds()
    {
        // A not-IsConfident resolution (transient provider outage, or nothing installed matched) must SKIP semantic dedup
        // entirely — no candidate flagged, no embedding attempted — so a distinct and a paraphrase BOTH survive as they
        // would today. This is the proof that an outage never mass-dedups legitimate new memories.
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("paraphrased helper lesson", 1f, 0f, 0f);
        var deduplicator = Build(vectors, out var provider, isConfident: false);

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("paraphrased helper lesson", MemoryScope.Procedural)],
            CancellationToken.None);

        AssertEx.False(result.Applied, "A not-confident embedding model degrades to lexical-only (NOT applied).");
        AssertEx.Equal(expected: 0, result.DuplicateIndexes.Count, "Nothing may be flagged when semantic dedup does not run.");
        AssertEx.Equal(expected: 0, provider.CreateGeneratorCallCount, "A not-confident resolution must never construct an embedding generator.");
    }

    [Test]
    public async Task FindSemanticDuplicates_WhenEmbedderThrows_ReturnsNotApplied()
    {
        // Any node-local embedding failure degrades to NOT-applied (lexical-only), never throwing into the extraction run.
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("paraphrased helper lesson", 1f, 0f, 0f);
        var deduplicator = Build(vectors, out var provider);
        provider.ThrowOnGenerate = true;

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("paraphrased helper lesson", MemoryScope.Procedural)],
            CancellationToken.None);

        AssertEx.False(result.Applied, "An embedding transport failure degrades to lexical-only.");
        AssertEx.Equal(expected: 0, result.DuplicateIndexes.Count);
    }

    [Test]
    public async Task FindSemanticDuplicates_ThresholdIsTheDecidingKnob()
    {
        // A candidate at cosine ~0.9 against the existing memory: below a 0.95 threshold it is KEPT; at/below a 0.85
        // threshold it is FLAGGED. Same vectors, only the threshold changes — proving the threshold governs the drop.
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("mid-similarity lesson", 0.9f, 0.4358898943540674f, 0f); // unit vector, cosine 0.9 with the existing one

        var strict = Build(vectors, out _, threshold: 0.95d);
        var strictResult = await strict.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("mid-similarity lesson", MemoryScope.Procedural)],
            CancellationToken.None);
        AssertEx.Equal(expected: 0, strictResult.DuplicateIndexes.Count, "Below a strict 0.95 threshold the mid-similarity candidate is kept.");

        var loose = Build(vectors, out _, threshold: 0.85d);
        var looseResult = await loose.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("mid-similarity lesson", MemoryScope.Procedural)],
            CancellationToken.None);
        AssertEx.True(looseResult.DuplicateIndexes.Contains(0), "At/above a loose 0.85 threshold the same candidate is flagged.");
    }

    [Test]
    public async Task FindSemanticDuplicates_ConfinesComparisonToSameScope()
    {
        // Same wording/vector but a DIFFERENT scope must NOT dedupe — a Failure-scope "what not to do" paraphrase of a
        // Procedural memory is a distinct lesson (mirrors the lexical key including scope).
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("paraphrased helper lesson", 1f, 0f, 0f);
        var deduplicator = Build(vectors, out _);

        var result = await deduplicator.FindSemanticDuplicatesAsync([Existing("existing helper lesson", MemoryScope.Procedural)],
            [Candidate("paraphrased helper lesson", MemoryScope.Failure)],
            CancellationToken.None);

        AssertEx.Equal(expected: 0, result.DuplicateIndexes.Count, "A cross-scope paraphrase is not a duplicate.");
    }

    [Test]
    public async Task FindSemanticDuplicates_CachesExistingVectors_ButReEmbedsCandidateEachRun()
    {
        var vectors = new VectorMap()
                      .Set("existing helper lesson", 1f, 0f, 0f)
                      .Set("some new candidate", 0f, 1f, 0f);
        var deduplicator = Build(vectors, out var provider);
        var existing = new[]
        {
            Existing("existing helper lesson", MemoryScope.Procedural)
        };
        var candidates = new[]
        {
            Candidate("some new candidate", MemoryScope.Procedural)
        };

        await deduplicator.FindSemanticDuplicatesAsync(existing, candidates, CancellationToken.None);
        var afterFirst = provider.TotalEmbeddedTexts;
        await deduplicator.FindSemanticDuplicatesAsync(existing, candidates, CancellationToken.None);
        var afterSecond = provider.TotalEmbeddedTexts;

        AssertEx.Equal(expected: 2, afterFirst, "First run embeds the existing memory plus the candidate.");
        AssertEx.Equal(afterFirst + 1, afterSecond, "Second run serves the cached existing vector and only re-embeds the candidate.");
    }

    private static MemorySemanticDeduplicator Build(VectorMap vectors,
        out MapEmbeddingProvider provider,
        bool isConfident = true,
        double threshold = 0.92d)
    {
        provider = new MapEmbeddingProvider(vectors);
        var resolver = SingleProviderResolverFactory.Create(provider);
        var options = Options.Create(new MemoryExtractionOptions
        {
            SemanticDedupEmbeddingProviderName = MapEmbeddingProvider.ProviderKey,
            SemanticDedupSimilarityThreshold = threshold,
            SemanticDedupEmbeddingCacheMaxEntries = 512
        });
        return new MemorySemanticDeduplicator(resolver,
            new FakeEmbeddingModelResolver(isConfident),
            options,
            NullLogger<MemorySemanticDeduplicator>.Instance);
    }

    private static MemoryDedupExisting Existing(string behavior, MemoryScope scope)
    {
        return new MemoryDedupExisting(Guid.NewGuid(), Version: 1, scope, behavior);
    }

    private static MemoryDedupCandidate Candidate(string behavior, MemoryScope scope)
    {
        return new MemoryDedupCandidate(scope, behavior);
    }

    // Deterministic text -> vector map so cosine (and thus threshold behaviour) is fully controllable and Ollama-free.
    private sealed class VectorMap
    {
        private readonly Dictionary<string, float[]> _byText = new(StringComparer.Ordinal);

        public VectorMap Set(string text, params float[] vector)
        {
            _byText[text] = vector;
            return this;
        }

        public float[] Get(string text)
        {
            return _byText.TryGetValue(text, out var vector)
                ? vector
                : throw new InvalidOperationException($"No fake vector configured for '{text}'.");
        }
    }

    // A node-local provider whose embedding generator returns the mapped vector for each input text; records generator
    // construction and total texts embedded so the cache / re-embed assertions can observe the round-trips.
    private sealed class MapEmbeddingProvider(VectorMap vectors) : ILocalModelProvider
    {
        public const string ProviderKey = "fake-memdedup";

        public int CreateGeneratorCallCount { get; private set; }

        public int TotalEmbeddedTexts { get; private set; }

        public bool ThrowOnGenerate { get; set; }

        public string ProviderName => ProviderKey;

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            CreateGeneratorCallCount++;
            return new MapEmbeddingGenerator(this, vectors);
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

        private void RecordEmbedded()
        {
            TotalEmbeddedTexts++;
        }

        private sealed class MapEmbeddingGenerator(MapEmbeddingProvider owner, VectorMap vectors) : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                if (owner.ThrowOnGenerate)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                var embeddings = new List<Embedding<float>>();
                foreach (var value in values)
                {
                    owner.RecordEmbedded();
                    embeddings.Add(new Embedding<float>(vectors.Get(value)));
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
        }
    }

    // A fake resolver so IsConfident is controllable without touching the provider's ListModelsAsync.
    private sealed class FakeEmbeddingModelResolver(bool isConfident) : IEmbeddingModelResolver
    {
        public Task<EmbeddingModelResolution> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EmbeddingModelResolution(ResolvedModel, isConfident));
        }
    }
}
