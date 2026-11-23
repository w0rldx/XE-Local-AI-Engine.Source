namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>A Qwen3.6-35B-A3B-like <c>qwen35moe</c> tensor list: one row per tensor of a linear and of a full block, with its HF name and value relation.</summary>
/// <remarks>
///     Names, shapes and types as a public Q4_K_M file's header lists them: 40 blocks (every fourth full), 256 experts of FFN
///     512, hidden 2048, 16 key and 32 value heads, no MTP block, 733 tensors. That file mixes Q5_K and Q6_K for
///     <c>ffn_down_exps</c>; here linear blocks carry Q5_K and full blocks Q6_K. Relations are the expected final ones.
/// </remarks>
internal static class Qwen35MoeTensorFixture
{
    public const int BlockCount = 40;

    public static readonly Qwen35TensorFixture.Row[] LinearBlock =
    [
        R("attn_gate.weight", "linear_attn.in_proj_z.weight", [2048, 4096], "Q8_0", "reordered"),
        R("attn_norm.weight", "input_layernorm.weight", [2048], "F32", "plus_one"),
        R("attn_qkv.weight", "linear_attn.in_proj_qkv.weight", [2048, 8192], "Q8_0", "reordered"),
        R("ffn_down_exps.weight", "mlp.experts.down_proj", [512, 2048, 256], "Q5_K", "quantized"),
        R("ffn_down_shexp.weight", "mlp.shared_expert.down_proj.weight", [512, 2048], "Q8_0", "quantized"),
        R("ffn_gate_exps.weight", "mlp.experts.gate_up_proj", [2048, 512, 256], "Q4_K", "sliced"),
        R("ffn_gate_inp.weight", "mlp.gate.weight", [2048, 256], "F32", "equal"),
        R("ffn_gate_inp_shexp.weight", "mlp.shared_expert_gate.weight", [2048], "F32", "equal"),
        R("ffn_gate_shexp.weight", "mlp.shared_expert.gate_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("ffn_up_exps.weight", "mlp.experts.gate_up_proj", [2048, 512, 256], "Q4_K", "sliced"),
        R("ffn_up_shexp.weight", "mlp.shared_expert.up_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("post_attention_norm.weight", "post_attention_layernorm.weight", [2048], "F32", "plus_one"),
        R("ssm_a", "linear_attn.A_log", [32], "F32", "reordered"),
        R("ssm_alpha.weight", "linear_attn.in_proj_a.weight", [2048, 32], "F32", "reordered"),
        R("ssm_beta.weight", "linear_attn.in_proj_b.weight", [2048, 32], "F32", "reordered"),
        R("ssm_conv1d.weight", "linear_attn.conv1d.weight", [4, 8192], "F32", "reordered"),
        R("ssm_dt.bias", "linear_attn.dt_bias", [32], "F32", "reordered"),
        R("ssm_norm.weight", "linear_attn.norm.weight", [128], "F32", "equal"),
        R("ssm_out.weight", "linear_attn.out_proj.weight", [4096, 2048], "Q8_0", "reordered")
    ];

    public static readonly Qwen35TensorFixture.Row[] FullBlock =
    [
        R("attn_k.weight", "self_attn.k_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("attn_k_norm.weight", "self_attn.k_norm.weight", [256], "F32", "plus_one"),
        R("attn_norm.weight", "input_layernorm.weight", [2048], "F32", "plus_one"),
        R("attn_output.weight", "self_attn.o_proj.weight", [4096, 2048], "Q8_0", "quantized"),
        R("attn_q.weight", "self_attn.q_proj.weight", [2048, 8192], "Q8_0", "quantized"),
        R("attn_q_norm.weight", "self_attn.q_norm.weight", [256], "F32", "plus_one"),
        R("attn_v.weight", "self_attn.v_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("ffn_down_exps.weight", "mlp.experts.down_proj", [512, 2048, 256], "Q6_K", "quantized"),
        R("ffn_down_shexp.weight", "mlp.shared_expert.down_proj.weight", [512, 2048], "Q8_0", "quantized"),
        R("ffn_gate_exps.weight", "mlp.experts.gate_up_proj", [2048, 512, 256], "Q4_K", "sliced"),
        R("ffn_gate_inp.weight", "mlp.gate.weight", [2048, 256], "F32", "equal"),
        R("ffn_gate_inp_shexp.weight", "mlp.shared_expert_gate.weight", [2048], "F32", "equal"),
        R("ffn_gate_shexp.weight", "mlp.shared_expert.gate_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("ffn_up_exps.weight", "mlp.experts.gate_up_proj", [2048, 512, 256], "Q4_K", "sliced"),
        R("ffn_up_shexp.weight", "mlp.shared_expert.up_proj.weight", [2048, 512], "Q8_0", "quantized"),
        R("post_attention_norm.weight", "post_attention_layernorm.weight", [2048], "F32", "plus_one")
    ];

    /// <summary>The MoE template's node ids between <c>post_norm</c> and <c>ffn_residual</c>; the frontend's explanation keys depend on them.</summary>
    public static readonly string[] MoeNodeIds =
    [
        "router", "router_probs", "topk", "topk_weights", "exp_gate", "exp_up", "exp_act", "exp_down", "exp_weighted", "shexp_gate", "shexp_up",
        "shexp_act", "shexp_down", "shexp_scale", "shexp_gated", "moe_out"
    ];

    /// <summary>The MoE FFN's edges, from <c>post_norm</c> into <c>ffn_residual</c>, as <c>from-&gt;to</c>.</summary>
    public static readonly string[] MoeEdges =
    [
        "post_norm->router", "router->router_probs", "router_probs->topk", "router_probs->topk_weights", "topk->topk_weights",
        "post_norm->exp_gate", "post_norm->exp_up", "post_norm->shexp_gate", "post_norm->shexp_up", "post_norm->shexp_scale",
        "topk->exp_gate", "topk->exp_up", "topk->exp_down", "exp_gate->exp_act", "exp_up->exp_act", "exp_act->exp_down",
        "exp_down->exp_weighted", "topk_weights->exp_weighted", "shexp_gate->shexp_act", "shexp_up->shexp_act", "shexp_act->shexp_down",
        "shexp_down->shexp_gated", "shexp_scale->shexp_gated", "exp_weighted->moe_out", "shexp_gated->moe_out", "moe_out->ffn_residual"
    ];

    /// <summary>40 trunk blocks, an untied <c>output.weight</c>, 256 experts, 16 key and 32 value heads; with the MTP block <c>blk.40</c> on request.</summary>
    public static GgufTensorInspection Qwen36_35B_A3B(bool withMtpBlock = false)
    {
        var trunk = Enumerable.Range(0, BlockCount).SelectMany(static block => BlockTensors(block, Qwen35TensorFixture.IsFull(block) ? FullBlock : LinearBlock));
        var mtp = withMtpBlock
            ? BlockTensors(BlockCount, FullBlock).Concat(Qwen35TensorFixture.NextnTensors.Select(static name => Qwen35TensorFixture.Tensor($"blk.{BlockCount}.{name}", [2048], "F32")))
            : Enumerable.Empty<GgufTensorInfo>();
        return Qwen35TensorFixture.Inspection("qwen35moe",
            new[]
                {
                    Qwen35TensorFixture.Tensor("output.weight", [2048, 248_320], "Q6_K"),
                    Qwen35TensorFixture.Tensor("output_norm.weight", [2048], "F32"),
                    Qwen35TensorFixture.Tensor("token_embd.weight", [2048, 248_320], "Q8_0")
                }
                .Concat(trunk)
                .Concat(mtp),
            ssmGroupCount: 16, ssmTimeStepRank: 32, expertCount: 256);
    }

    public static IEnumerable<GgufTensorInfo> BlockTensors(int block, Qwen35TensorFixture.Row[] rows) =>
        rows.Select(row => Qwen35TensorFixture.Tensor($"blk.{block}.{row.GgufSuffix}", row.Shape, row.GgmlType));

    private static Qwen35TensorFixture.Row R(string ggufSuffix, string hfSuffix, long[] shape, string ggmlType, string relation) =>
        new()
        {
            GgufSuffix = ggufSuffix,
            HfSuffix = hfSuffix,
            Shape = shape,
            GgmlType = ggmlType,
            Relation = relation
        };
}
