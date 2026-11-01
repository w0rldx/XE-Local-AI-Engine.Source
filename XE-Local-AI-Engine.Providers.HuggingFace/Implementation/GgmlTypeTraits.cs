namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>The name, elements per block and bytes per block of one <c>ggml_type</c>, as llama.cpp's <c>type_traits</c> table declares them.</summary>
/// <remarks>
///     Read from <c>ggml.h</c> (<c>enum ggml_type</c>), <c>ggml.c</c> (<c>type_traits</c>) and the block-size asserts in
///     <c>ggml-common.h</c> at the pinned llama.cpp tag. Removed ids (4, 5, 31–33, 36–38) and later additions are unknown.
/// </remarks>
internal readonly record struct GgmlTypeTraits(string Name, long BlockSize, long TypeSize)
{
    private const long QkK = 256;

    private static readonly Dictionary<uint, GgmlTypeTraits> ById = new()
    {
        [0] = new("F32", 1, 4),
        [1] = new("F16", 1, 2),
        [2] = new("Q4_0", 32, 18),
        [3] = new("Q4_1", 32, 20),
        [6] = new("Q5_0", 32, 22),
        [7] = new("Q5_1", 32, 24),
        [8] = new("Q8_0", 32, 34),
        [9] = new("Q8_1", 32, 36),
        [10] = new("Q2_K", QkK, 84),
        [11] = new("Q3_K", QkK, 110),
        [12] = new("Q4_K", QkK, 144),
        [13] = new("Q5_K", QkK, 176),
        [14] = new("Q6_K", QkK, 210),
        [15] = new("Q8_K", QkK, 292),
        [16] = new("IQ2_XXS", QkK, 66),
        [17] = new("IQ2_XS", QkK, 74),
        [18] = new("IQ3_XXS", QkK, 98),
        [19] = new("IQ1_S", QkK, 50),
        [20] = new("IQ4_NL", 32, 18),
        [21] = new("IQ3_S", QkK, 110),
        [22] = new("IQ2_S", QkK, 82),
        [23] = new("IQ4_XS", QkK, 136),
        [24] = new("I8", 1, 1),
        [25] = new("I16", 1, 2),
        [26] = new("I32", 1, 4),
        [27] = new("I64", 1, 8),
        [28] = new("F64", 1, 8),
        [29] = new("IQ1_M", QkK, 56),
        [30] = new("BF16", 1, 2),
        [34] = new("TQ1_0", QkK, 54),
        [35] = new("TQ2_0", QkK, 66),
        [39] = new("MXFP4", 32, 17),
        [40] = new("NVFP4", 64, 36),
        [41] = new("Q1_0", 128, 18),
        [42] = new("Q2_0", 64, 18),
        [43] = new("F8_E4M3", 1, 1)
    };

    /// <summary>The traits of <paramref name="ggmlType" />, or <see langword="null" /> for an id the pinned table does not ship.</summary>
    public static GgmlTypeTraits? Find(uint ggmlType)
    {
        return ById.TryGetValue(ggmlType, out var traits) ? traits : null;
    }
}
