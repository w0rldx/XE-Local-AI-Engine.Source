namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>A Llama-3.2-1B-like <c>llama</c> tensor list: one row per tensor of a block, with its HF name and value relation.</summary>
/// <remarks>
///     Names follow llama.cpp <c>tensor_mapping.py</c> and <c>constants.py</c> at b12014; shapes the public Llama 3.2 1B config
///     (hidden 2048, intermediate 8192, 16 layers, 32 heads, 8 KV heads, head dim 64, vocab 128256, tied embeddings, llama3
///     RoPE). The storage types are illustrative of a Q4_K_M file, not read from one.
/// </remarks>
internal static class LlamaTensorFixture
{
    public const int BlockCount = 16;

    public static readonly Qwen35TensorFixture.Row[] Block =
    [
        R("attn_norm.weight", "input_layernorm.weight", [2048], "F32", "equal"),
        R("attn_q.weight", "self_attn.q_proj.weight", [2048, 2048], "Q4_K", "reordered"),
        R("attn_k.weight", "self_attn.k_proj.weight", [2048, 512], "Q4_K", "reordered"),
        R("attn_v.weight", "self_attn.v_proj.weight", [2048, 512], "Q6_K", "quantized"),
        R("attn_output.weight", "self_attn.o_proj.weight", [2048, 2048], "Q4_K", "quantized"),
        R("ffn_norm.weight", "post_attention_layernorm.weight", [2048], "F32", "equal"),
        R("ffn_gate.weight", "mlp.gate_proj.weight", [2048, 8192], "Q4_K", "quantized"),
        R("ffn_up.weight", "mlp.up_proj.weight", [2048, 8192], "Q4_K", "quantized"),
        R("ffn_down.weight", "mlp.down_proj.weight", [8192, 2048], "Q6_K", "quantized")
    ];

    /// <summary>The template's node ids in order; the frontend's explanation keys depend on them.</summary>
    public static readonly string[] TemplateNodeIds =
    [
        "input", "attn_norm", "q", "k", "v", "rope", "attn", "mixer_out", "attn_residual", "post_norm", "ffn_gate", "ffn_up", "ffn_act",
        "ffn_down", "ffn_residual", "control_vector", "output"
    ];

    /// <summary>The template's edges in order, the same for every Q/K/V layout.</summary>
    public static readonly string[] TemplateEdges =
    [
        "input->attn_norm", "attn_norm->q", "attn_norm->k", "attn_norm->v", "q->rope", "k->rope", "rope->attn", "v->attn", "attn->mixer_out",
        "mixer_out->attn_residual", "input->attn_residual", "attn_residual->post_norm", "post_norm->ffn_gate", "post_norm->ffn_up", "ffn_gate->ffn_act",
        "ffn_up->ffn_act", "ffn_act->ffn_down", "ffn_down->ffn_residual", "attn_residual->ffn_residual", "ffn_residual->control_vector", "control_vector->output"
    ];

    /// <summary>16 dense blocks, <c>token_embd</c>, <c>output_norm</c> and <c>rope_freqs</c>; tied, so no <c>output.weight</c>.</summary>
    public static GgufTensorInspection Llama3_2_1B() =>
        Qwen35TensorFixture.Inspection("llama",
            new[]
                {
                    Qwen35TensorFixture.Tensor("token_embd.weight", [2048, 128_256], "Q6_K"),
                    Qwen35TensorFixture.Tensor("rope_freqs.weight", [32], "F32")
                }
                .Concat(Enumerable.Range(0, BlockCount).SelectMany(static block => Block.Select(row => Qwen35TensorFixture.Tensor($"blk.{block}.{row.GgufSuffix}", row.Shape, row.GgmlType))))
                .Concat([Qwen35TensorFixture.Tensor("output_norm.weight", [2048], "F32")]));

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
