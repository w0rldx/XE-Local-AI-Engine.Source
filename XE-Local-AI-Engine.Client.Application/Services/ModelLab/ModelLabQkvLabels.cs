namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Picks a template by how a block kind stores Q, K and V, and holds the Q/K/V labels attention templates share.</summary>
/// <remarks>
///     llama.cpp <c>create_tensor_qkv</c> decides per layer: one fused <c>attn_qkv</c> or separate <c>attn_q/k/v</c>, and
///     <c>build_qkv</c> slices the fused result into the same Q, K and V. The graph is the same, only the labels differ.
/// </remarks>
internal static class ModelLabQkvLabels
{
    public const string Query = "Query projection · attn_q";
    public const string Key = "Key projection · attn_k";
    public const string Value = "Value projection · attn_v";
    public const string FusedQuery = "Query rows of the fused projection · attn_qkv";
    public const string FusedKey = "Key rows of the fused projection · attn_qkv";
    public const string FusedValue = "Value rows of the fused projection · attn_qkv";

    // A file mixing both layouts gets labels true for either.
    public const string MixedQuery = "Query projection · attn_q or Q rows of attn_qkv";
    public const string MixedKey = "Key projection · attn_k or K rows of attn_qkv";
    public const string MixedValue = "Value projection · attn_v or V rows of attn_qkv";

    // The ModelLabArchitectureTemplate.QkvLayout values; the client keys its German Q/K/V headings on them.
    public const string SeparateLayout = "separate";
    public const string FusedLayout = "fused";
    public const string MixedLayout = "mixed";

    /// <summary>
    ///     Among the blocks of <paramref name="kind" />, all with an <c>attn_qkv.weight</c> give <paramref name="fused" />, all with an
    ///     <c>attn_q.weight</c> give <paramref name="separate" />, anything else gives <paramref name="mixed" />; null when no block is of that kind.
    ///     The template returned carries the layout in <see cref="ModelLabArchitectureTemplate.QkvLayout" />.
    /// </summary>
    public static ModelLabArchitectureTemplate? ByLayout(IReadOnlyList<ModelLabArchitectureBlock> blocks, string kind, ModelLabArchitectureTemplate separate,
        ModelLabArchitectureTemplate fused, ModelLabArchitectureTemplate mixed)
    {
        var ofKind = blocks.Where(block => block.Kind == kind).ToList();
        if (ofKind.Count == 0)
        {
            return null;
        }

        // By the weight: a fused attn_qkv.weight may sit beside separate attn_q/k/v biases (create_tensor_qkv). A split-file part can
        // hold a block with neither weight, so only positive evidence counts and such a block makes the layout mixed.
        if (ofKind.All(static block => HasWeight(block, ".attn_qkv.weight")))
        {
            return fused with
            {
                QkvLayout = FusedLayout
            };
        }

        return ofKind.All(static block => HasWeight(block, ".attn_q.weight"))
            ? separate with
            {
                QkvLayout = SeparateLayout
            }
            : mixed with
            {
                QkvLayout = MixedLayout
            };
    }

    private static bool HasWeight(ModelLabArchitectureBlock block, string suffix) =>
        block.Groups.SelectMany(static group => group.Tensors).Any(tensor => tensor.GgufName.EndsWith(suffix, StringComparison.Ordinal));
}
