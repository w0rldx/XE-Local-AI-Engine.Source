namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
///     Provider-neutral post-generation vector policy shared by knowledge ingestion and query embedding. For a confidently
///     resolved Nomic v1.5 model, the default policy implements the model card's Matryoshka recipe exactly: population
///     layer normalization over the full native vector (epsilon 1e-5), truncation to the first 512 components, then L2
///     normalization. Other models and explicit native mode preserve the provider-native vector.
/// </summary>
public static class KnowledgeEmbeddingVectorPolicy
{
    public const int MatryoshkaWidth = 512;
    public const string LegacyIdentity = "legacy:unversioned";

    private const double LayerNormEpsilon = 1e-5;
    private const string MatryoshkaAlgorithm = "layernorm-population-eps1e-5-truncate-l2:v1";
    private const string NativeAlgorithm = "native:v1";
    private const string InvalidVectorReason = "The embedding model returned an invalid vector. Reindex with a supported embedding model.";
    private const string ShortVectorReason = "The Nomic embedding vector is shorter than the configured 512-dimension policy. Reindex in native mode or use Nomic v1.5.";

    /// <summary>Transforms one provider-produced vector and returns its canonical identity and width.</summary>
    public static KnowledgeEmbeddingVector Transform(EmbeddingModelResolution resolution,
        ReadOnlyMemory<float> nativeVector,
        KnowledgeEmbeddingVectorMode mode)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var source = nativeVector.Span;
        ValidateFinite(source);

        if (!ShouldApplyMatryoshka(resolution, mode))
        {
            var native = nativeVector.ToArray();
            return new KnowledgeEmbeddingVector(native,
                CreateIdentity(resolution.Name, resolution.RevisionFingerprint, NativeAlgorithm, native.Length));
        }

        if (source.Length < MatryoshkaWidth)
        {
            throw new KnowledgeIngestionException(ShortVectorReason);
        }

        var mean = 0d;
        for (var index = 0; index < source.Length; index++)
        {
            mean += source[index];
        }

        mean /= source.Length;

        var variance = 0d;
        for (var index = 0; index < source.Length; index++)
        {
            var centered = source[index] - mean;
            variance += centered * centered;
        }

        variance /= source.Length;
        var inverseStandardDeviation = 1d / Math.Sqrt(variance + LayerNormEpsilon);

        var transformed = new float[MatryoshkaWidth];
        var squaredMagnitude = 0d;
        for (var index = 0; index < transformed.Length; index++)
        {
            var value = (float)((source[index] - mean) * inverseStandardDeviation);
            transformed[index] = value;
            squaredMagnitude += (double)value * value;
        }

        if (squaredMagnitude > 0d)
        {
            var inverseMagnitude = 1d / Math.Sqrt(squaredMagnitude);
            for (var index = 0; index < transformed.Length; index++)
            {
                transformed[index] = (float)(transformed[index] * inverseMagnitude);
            }
        }

        return new KnowledgeEmbeddingVector(transformed,
            CreateIdentity(resolution.Name, resolution.RevisionFingerprint, MatryoshkaAlgorithm, MatryoshkaWidth));
    }

    /// <summary>Serializes a transformed vector deterministically in the platform's established native float32 byte order.</summary>
    public static byte[] ToBytes(KnowledgeEmbeddingVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return MemoryMarshal.AsBytes(vector.Values.Span).ToArray();
    }

    /// <summary>
    ///     Returns the exact current identity when its width is policy-known. Native width is provider-output-dependent, so
    ///     callers comparing catalog rows should use <see cref="MatchesCurrentPolicy" /> instead.
    /// </summary>
    public static string? TryCreateExpectedIdentity(EmbeddingModelResolution resolution, KnowledgeEmbeddingVectorMode mode)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return ShouldApplyMatryoshka(resolution, mode)
            ? CreateIdentity(resolution.Name, resolution.RevisionFingerprint, MatryoshkaAlgorithm, MatryoshkaWidth)
            : null;
    }

    /// <summary>
    ///     Returns the stable pre-generation cache family for the resolved model and active policy. Matryoshka width is
    ///     policy-fixed, while native width remains in the cached entry's exact canonical identity.
    /// </summary>
    public static string CreateCacheFamilyIdentity(EmbeddingModelResolution resolution, KnowledgeEmbeddingVectorMode mode)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return ShouldApplyMatryoshka(resolution, mode)
            ? CreateIdentity(resolution.Name, resolution.RevisionFingerprint, MatryoshkaAlgorithm, MatryoshkaWidth)
            : string.Create(CultureInfo.InvariantCulture,
                $"{resolution.Name}@{resolution.RevisionFingerprint}::{NativeAlgorithm}");
    }

    /// <summary>Checks whether a persisted identity belongs to the currently resolved model and configured policy.</summary>
    public static bool MatchesCurrentPolicy(string storedIdentity,
        int storedWidth,
        EmbeddingModelResolution resolution,
        KnowledgeEmbeddingVectorMode mode)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (string.IsNullOrWhiteSpace(storedIdentity) || storedWidth <= 0)
        {
            return false;
        }

        var expected = TryCreateExpectedIdentity(resolution, mode);
        if (expected is not null)
        {
            return storedWidth == MatryoshkaWidth && string.Equals(storedIdentity, expected, StringComparison.Ordinal);
        }

        return string.Equals(storedIdentity,
            CreateIdentity(resolution.Name, resolution.RevisionFingerprint, NativeAlgorithm, storedWidth),
            StringComparison.Ordinal);
    }

    private static bool ShouldApplyMatryoshka(EmbeddingModelResolution resolution, KnowledgeEmbeddingVectorMode mode)
    {
        return mode == KnowledgeEmbeddingVectorMode.Matryoshka512
               && resolution.IsConfident
               && IsNomicV15(resolution.Name);
    }

    private static bool IsNomicV15(string resolvedModel)
    {
        return resolvedModel.Contains("nomic-embed-text-v1.5", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateIdentity(string resolvedModel, string revisionFingerprint, string algorithm, int width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionFingerprint);
        return string.Create(CultureInfo.InvariantCulture, $"{resolvedModel}@{revisionFingerprint}::{algorithm}:{width}");
    }

    private static void ValidateFinite(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new KnowledgeIngestionException(InvalidVectorReason);
        }

        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new KnowledgeIngestionException(InvalidVectorReason);
            }
        }
    }
}

/// <summary>A post-policy vector plus the canonical model/algorithm/version/width identity that produced it.</summary>
public sealed record KnowledgeEmbeddingVector(ReadOnlyMemory<float> Values, string Identity)
{
    public int Dimension => Values.Length;
}
