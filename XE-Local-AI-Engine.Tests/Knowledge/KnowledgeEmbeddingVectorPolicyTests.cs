namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class KnowledgeEmbeddingVectorPolicyTests
{
    private const string NomicV15 = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M";

    [Test]
    public void Transform_ConfidentNomicV15_AppliesPopulationLayerNormThenTruncatesAndL2Normalizes()
    {
        var native = new float[KnowledgeEmbeddingVectorPolicy.MatryoshkaWidth];
        native[0] = 1f;

        var transformed = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution(NomicV15, IsConfident: true),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);

        var expectedFirst = MathF.Sqrt(511f / 512f);
        var expectedOther = -1f / MathF.Sqrt(512f * 511f);
        AssertEx.Equal(512, transformed.Dimension);
        AssertEx.True(MathF.Abs(transformed.Values.Span[0] - expectedFirst) < 1e-6f);
        AssertEx.True(MathF.Abs(transformed.Values.Span[1] - expectedOther) < 1e-6f);
        AssertEx.True(transformed.Identity.Contains("layernorm-population-eps1e-5-truncate-l2:v1:512", StringComparison.Ordinal));
    }

    [Test]
    public void Transform_ConstantVector_UsesEpsilonAndLeavesAStableZeroVector()
    {
        var native = Enumerable.Repeat(7f, 768).ToArray();

        var transformed = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution(NomicV15, IsConfident: true),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.True(transformed.Values.Span.IndexOfAnyExcept(0f) < 0, "A constant vector has zero centered signal and must stay exactly zero.");
    }

    [Test]
    public void Transform_SameInputTwice_ProducesDeterministicBytesAndIdentity()
    {
        var native = Enumerable.Range(0, 768).Select(static value => (float)Math.Sin(value)).ToArray();
        var resolution = new EmbeddingModelResolution(NomicV15, IsConfident: true);

        var ingestion = KnowledgeEmbeddingVectorPolicy.Transform(resolution, native, KnowledgeEmbeddingVectorMode.Matryoshka512);
        var query = KnowledgeEmbeddingVectorPolicy.Transform(resolution, native, KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.Equal(ingestion.Identity, query.Identity);
        AssertEx.True(KnowledgeEmbeddingVectorPolicy.ToBytes(ingestion).AsSpan()
                                                    .SequenceEqual(KnowledgeEmbeddingVectorPolicy.ToBytes(query)),
            "The shared ingestion/query seam must serialize identical input to identical bytes.");
    }

    [Test]
    public void Transform_SameModelNameWithDifferentInstalledRevision_UsesDifferentCanonicalIdentity()
    {
        var native = Enumerable.Range(0, 768).Select(static value => value / 100f).ToArray();
        var original = KnowledgeEmbeddingVectorPolicy.Transform(
            new EmbeddingModelResolution(NomicV15, IsConfident: true, RevisionFingerprint: "inventory-v1:original"),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);
        var replacementResolution = new EmbeddingModelResolution(NomicV15,
            IsConfident: true,
            RevisionFingerprint: "inventory-v1:replacement");
        var replacement = KnowledgeEmbeddingVectorPolicy.Transform(replacementResolution,
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.NotEqual(original.Identity, replacement.Identity,
            "Replacing installed weights under the same name must invalidate stored vectors and cache entries.");
        AssertEx.False(KnowledgeEmbeddingVectorPolicy.MatchesCurrentPolicy(original.Identity,
                original.Dimension,
                replacementResolution,
                KnowledgeEmbeddingVectorMode.Matryoshka512),
            "The catalog staleness check must reject vectors built by the previous installed revision.");
    }

    [Test]
    public void Transform_NonNomicOrExplicitNative_PreservesNativeWidthAndUsesDistinctRollbackIdentity()
    {
        var native = Enumerable.Range(0, 768).Select(static value => value / 100f).ToArray();
        var nonNomic = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution("bge-m3", IsConfident: true),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);
        var rollback = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution(NomicV15, IsConfident: true),
            native,
            KnowledgeEmbeddingVectorMode.Native);
        var matryoshka = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution(NomicV15, IsConfident: true),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.Equal(768, nonNomic.Dimension);
        AssertEx.Equal(768, rollback.Dimension);
        AssertEx.True(nonNomic.Values.Span.SequenceEqual(native));
        AssertEx.True(rollback.Values.Span.SequenceEqual(native));
        AssertEx.NotEqual(rollback.Identity, matryoshka.Identity);
        AssertEx.True(rollback.Identity.EndsWith("::native:v1:768", StringComparison.Ordinal));
    }

    [Test]
    public void Transform_NonConfidentNomic_PreservesNativeVector()
    {
        var native = Enumerable.Range(0, 768).Select(static value => (float)value).ToArray();

        var transformed = KnowledgeEmbeddingVectorPolicy.Transform(new EmbeddingModelResolution(NomicV15, IsConfident: false),
            native,
            KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.Equal(768, transformed.Dimension);
        AssertEx.True(transformed.Values.Span.SequenceEqual(native));
    }

    [Test]
    public void CreateCacheFamilyIdentity_IsolatesNativeAndMatryoshkaWhileLeavingNativeWidthInTheEntry()
    {
        var resolution = new EmbeddingModelResolution(NomicV15, IsConfident: true);

        var native = KnowledgeEmbeddingVectorPolicy.CreateCacheFamilyIdentity(resolution, KnowledgeEmbeddingVectorMode.Native);
        var matryoshka = KnowledgeEmbeddingVectorPolicy.CreateCacheFamilyIdentity(resolution, KnowledgeEmbeddingVectorMode.Matryoshka512);

        AssertEx.Equal($"{NomicV15}@unresolved::native:v1", native);
        AssertEx.Equal($"{NomicV15}@unresolved::layernorm-population-eps1e-5-truncate-l2:v1:512", matryoshka);
        AssertEx.NotEqual(native, matryoshka);
    }

    [Test]
    public void Transform_ShortOrNonFiniteNomic_ThrowsContentFreeReason()
    {
        var resolution = new EmbeddingModelResolution(NomicV15, IsConfident: true);

        var shortException = Capture(() => KnowledgeEmbeddingVectorPolicy.Transform(resolution,
            new float[511],
            KnowledgeEmbeddingVectorMode.Matryoshka512));
        var nonFinite = new float[512];
        nonFinite[123] = float.NaN;
        var nonFiniteException = Capture(() => KnowledgeEmbeddingVectorPolicy.Transform(resolution,
            nonFinite,
            KnowledgeEmbeddingVectorMode.Matryoshka512));

        AssertEx.False(shortException.Reason.Contains("511", StringComparison.Ordinal), "Failure reason must not disclose provider payload details.");
        AssertEx.False(nonFiniteException.Reason.Contains("NaN", StringComparison.Ordinal), "Failure reason must remain content-free.");
    }

    private static KnowledgeIngestionException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (KnowledgeIngestionException exception)
        {
            return exception;
        }

        throw new AssertionException("Expected KnowledgeIngestionException.");
    }
}
