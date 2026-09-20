namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Numerics.Tensors;
using System.Runtime.InteropServices;

/// <summary>
///     Vector-space helpers shared by the ingestion writer, the legacy-vector normalization backfill, and the managed
///     cosine search.
/// </summary>
/// <remarks>
///     The central operation is L2 normalization in place: cosine similarity is scale-invariant
///     (<c>cos(a,b) = dot(a,b) / (‖a‖·‖b‖)</c>), so rescaling a stored vector to unit length changes NO cosine result.
///     Normalizing every stored and query vector therefore lets the search score with a plain <c>TensorPrimitives.Dot</c>
///     instead of the three-accumulator <c>TensorPrimitives.CosineSimilarity</c> — one pass over the candidate rather
///     than three, with the two norms hoisted out of the inner loop.
/// </remarks>
internal static class KnowledgeVectorMath
{
    /// <summary>
    ///     Rescales <paramref name="vector" /> to unit L2 length in place and returns <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     A vector whose magnitude is zero or non-finite carries no direction to normalize; it is left byte-for-byte
    ///     unchanged and <see langword="false" /> is returned, so a zero embedding stays exactly zero and the search skips
    ///     such rows — matching <c>CosineSimilarity</c>, which returns <c>NaN</c> for a zero-magnitude vector.
    /// </remarks>
    public static bool NormalizeInPlace(Span<float> vector)
    {
        if (vector.IsEmpty)
        {
            return false;
        }

        var norm = TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm) || norm == 0f)
        {
            return false;
        }

        // Divide (not multiply-by-reciprocal): the direct divisor keeps the result within a float ULP of the reference
        // cosine's own dot/(‖a‖‖b‖), well inside the search's equivalence tolerance.
        TensorPrimitives.Divide(vector, norm, vector);
        return true;
    }

    /// <summary>
    ///     Reinterprets a native-byte-order <c>float32</c> embedding blob and normalizes it in place.
    /// </summary>
    /// <remarks>
    ///     That layout is the on-disk one the embedder wrote via
    ///     <see cref="MemoryMarshal.AsBytes{T}(System.ReadOnlySpan{T})" />. A blob whose length is not a whole number of
    ///     floats is left unchanged and <see langword="false" /> is returned: it is not a valid vector to rescale.
    /// </remarks>
    public static bool NormalizeBytesInPlace(Span<byte> embedding)
    {
        if (embedding.Length == 0 || embedding.Length % sizeof(float) != 0)
        {
            return false;
        }

        return NormalizeInPlace(MemoryMarshal.Cast<byte, float>(embedding));
    }
}
