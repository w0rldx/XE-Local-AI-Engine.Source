namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The Qwen3.5-0.8B Q4_K_M tensor list as the HF↔GGUF mapping spike recorded it: one row per tensor of a linear
///     block and of a full block, with the HF name and the value check the spike ran on the unquantized ones.
/// </summary>
/// <remarks>
///     The 0.8B repeats these two layouts (every fourth block, from <c>blk.3</c>, is full) and adds <c>token_embd</c> and
///     <c>output_norm</c>, 320 tensors. The 27B layout adds 40 more trunk blocks and the MTP block <c>blk.64</c>.
/// </remarks>
internal static class Qwen35TensorFixture
{
    public static readonly Row[] LinearBlock =
    [
        R("attn_norm.weight", "input_layernorm.weight", [1024], "F32", "plus_one"),
        R("ssm_a", "linear_attn.A_log", [16], "F32", "neg_exp"),
        R("ssm_conv1d.weight", "linear_attn.conv1d.weight", [4, 6144], "F32", "equal"),
        R("ssm_dt.bias", "linear_attn.dt_bias", [16], "F32", "equal"),
        R("ssm_alpha.weight", "linear_attn.in_proj_a.weight", [1024, 16], "Q8_0", "quantized"),
        R("ssm_beta.weight", "linear_attn.in_proj_b.weight", [1024, 16], "Q8_0", "quantized"),
        R("attn_qkv.weight", "linear_attn.in_proj_qkv.weight", [1024, 6144], "Q5_K", "quantized"),
        R("attn_gate.weight", "linear_attn.in_proj_z.weight", [1024, 2048], "Q4_K", "quantized"),
        R("ssm_norm.weight", "linear_attn.norm.weight", [128], "F32", "equal"),
        R("ssm_out.weight", "linear_attn.out_proj.weight", [2048, 1024], "Q5_K", "quantized"),
        R("ffn_down.weight", "mlp.down_proj.weight", [3584, 1024], "Q6_K", "quantized"),
        R("ffn_gate.weight", "mlp.gate_proj.weight", [1024, 3584], "Q4_K", "quantized"),
        R("ffn_up.weight", "mlp.up_proj.weight", [1024, 3584], "Q4_K", "quantized"),
        R("post_attention_norm.weight", "post_attention_layernorm.weight", [1024], "F32", "plus_one")
    ];

    public static readonly Row[] FullBlock =
    [
        R("attn_norm.weight", "input_layernorm.weight", [1024], "F32", "plus_one"),
        R("ffn_down.weight", "mlp.down_proj.weight", [3584, 1024], "Q4_K", "quantized"),
        R("ffn_gate.weight", "mlp.gate_proj.weight", [1024, 3584], "Q4_K", "quantized"),
        R("ffn_up.weight", "mlp.up_proj.weight", [1024, 3584], "Q4_K", "quantized"),
        R("post_attention_norm.weight", "post_attention_layernorm.weight", [1024], "F32", "plus_one"),
        R("attn_k_norm.weight", "self_attn.k_norm.weight", [256], "F32", "plus_one"),
        R("attn_k.weight", "self_attn.k_proj.weight", [1024, 512], "Q4_K", "quantized"),
        R("attn_output.weight", "self_attn.o_proj.weight", [2048, 1024], "Q4_K", "quantized"),
        R("attn_q_norm.weight", "self_attn.q_norm.weight", [256], "F32", "plus_one"),
        R("attn_q.weight", "self_attn.q_proj.weight", [1024, 4096], "Q4_K", "quantized"),
        R("attn_v.weight", "self_attn.v_proj.weight", [1024, 512], "Q4_K", "quantized")
    ];

    // The 27B's MTP block: a full-attention block plus the nextn head tensors (T3 report).
    public static readonly string[] NextnTensors =
        ["nextn.eh_proj.weight", "nextn.enorm.weight", "nextn.hnorm.weight", "nextn.shared_head_norm.weight"];

    public static bool IsFull(int block) => (block + 1) % 4 == 0;

    // The linear-attention tensors the converter permutes when the key and value head counts differ.
    public static readonly string[] ValueHeadReordered =
        ["attn_qkv.weight", "attn_gate.weight", "ssm_alpha.weight", "ssm_beta.weight", "ssm_a", "ssm_dt.bias", "ssm_conv1d.weight", "ssm_out.weight"];

    /// <summary>24 blocks, tied output (no <c>output.weight</c>), 16 key and 16 value heads as the spike read them.</summary>
    public static GgufTensorInspection Qwen35_0_8B(long? keyHeads = 16, long? valueHeads = 16) =>
        Inspection("qwen35", Trunk(24).Concat([NonBlock("output_norm.weight", [1024], "F32")]), ssmGroupCount: keyHeads, ssmTimeStepRank: valueHeads);

    /// <summary>64 trunk blocks, the MTP block <c>blk.64</c>, an untied <c>output.weight</c>, 16 key and 48 value heads.</summary>
    public static GgufTensorInspection Qwen35_27BLike()
    {
        var mtp = FullBlock.Select(row => Tensor($"blk.64.{row.GgufSuffix}", row.Shape, row.GgmlType))
                           .Concat(NextnTensors.Select(static name => Tensor($"blk.64.{name}", [1024], "F32")));
        return Inspection("qwen35",
            Trunk(64).Concat(mtp).Concat([NonBlock("output_norm.weight", [1024], "F32"), NonBlock("output.weight", [1024, 248_320], "Q6_K")]),
            ssmGroupCount: 16, ssmTimeStepRank: 48);
    }

    public static GgufTensorInspection Inspection(string? architecture, IEnumerable<GgufTensorInfo> tensors,
        GgufTensorReadFailure failure = GgufTensorReadFailure.None, long? ssmGroupCount = null, long? ssmTimeStepRank = null)
    {
        var list = tensors.ToList();
        return new GgufTensorInspection
        {
            Failure = failure,
            Version = 3,
            Architecture = architecture,
            ModelName = null,
            ParamCount = null,
            FileType = null,
            BlockCount = null,
            NextnPredictLayers = null,
            SsmGroupCount = ssmGroupCount,
            SsmTimeStepRank = ssmTimeStepRank,
            Alignment = null,
            AttentionHeadCount = null,
            AttentionHeadCountKv = null,
            EmbeddingLength = null,
            ContextLength = null,
            ExpertCount = null,
            ChatTemplate = null,
            SplitCount = null,
            TensorCount = list.Count,
            Tensors = list,
            HeaderBytes = 0,
            FileSizeBytes = 0
        };
    }

    // Every size is the element count, so a block's expected bytes are easy to sum in a test.
    public static GgufTensorInfo Tensor(string name, long[] shape, string ggmlType)
    {
        var elements = shape.Aggregate(1L, static (product, dimension) => product * dimension);
        return new GgufTensorInfo
        {
            Name = name,
            Dimensions = shape,
            GgmlType = 0,
            GgmlTypeName = ggmlType,
            Offset = 0,
            ElementCount = elements,
            ByteSize = elements
        };
    }

    private static IEnumerable<GgufTensorInfo> Trunk(int blocks) =>
        new[] { NonBlock("token_embd.weight", [1024, 248_320], "Q6_K") }
            .Concat(Enumerable.Range(0, blocks).SelectMany(block =>
                (IsFull(block) ? FullBlock : LinearBlock).Select(row => Tensor($"blk.{block}.{row.GgufSuffix}", row.Shape, row.GgmlType))));

    private static GgufTensorInfo NonBlock(string name, long[] shape, string ggmlType) => Tensor(name, shape, ggmlType);

    private static Row R(string ggufSuffix, string hfSuffix, long[] shape, string ggmlType, string relation) =>
        new() { GgufSuffix = ggufSuffix, HfSuffix = hfSuffix, Shape = shape, GgmlType = ggmlType, Relation = relation };

    /// <summary>One recorded tensor: the name after <c>blk.N.</c> on both sides, the GGUF shape and type, and the value relation.</summary>
    public sealed record Row
    {
        public required string GgufSuffix { get; init; }

        public required string HfSuffix { get; init; }

        public required long[] Shape { get; init; }

        public required string GgmlType { get; init; }

        public required string Relation { get; init; }
    }
}
