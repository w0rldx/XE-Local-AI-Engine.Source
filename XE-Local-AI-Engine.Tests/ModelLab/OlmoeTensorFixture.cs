namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>An OLMoE-1B-7B-like <c>olmoe</c> tensor list: one row per tensor of a block, with its HF name and value relation.</summary>
/// <remarks>
///     Names follow llama.cpp <c>src/models/olmoe.cpp</c> (<c>load_arch_tensors</c>) at b12014; shapes the public OLMoE-1B-7B config
///     (hidden 2048, expert FFN 1024, 64 experts, 16 layers, 16 heads and 16 KV heads, vocab 50304, untied head). The storage types
///     are illustrative of a Q4_K_M file, not read from one.
/// </remarks>
internal static class OlmoeTensorFixture
{
    public const int BlockCount = 16;

    public static readonly Qwen35TensorFixture.Row[] Block =
    [
        R("attn_norm.weight", "input_layernorm.weight", [2048], "F32", "equal"),
        R("attn_q.weight", "self_attn.q_proj.weight", [2048, 2048], "Q4_K", "quantized"),
        R("attn_k.weight", "self_attn.k_proj.weight", [2048, 2048], "Q4_K", "quantized"),
        R("attn_v.weight", "self_attn.v_proj.weight", [2048, 2048], "Q6_K", "quantized"),
        R("attn_output.weight", "self_attn.o_proj.weight", [2048, 2048], "Q4_K", "quantized"),
        R("attn_q_norm.weight", "self_attn.q_norm.weight", [2048], "F32", "equal"),
        R("attn_k_norm.weight", "self_attn.k_norm.weight", [2048], "F32", "equal"),
        R("ffn_norm.weight", "post_attention_layernorm.weight", [2048], "F32", "equal"),
        R("ffn_gate_inp.weight", "mlp.gate.weight", [2048, 64], "F32", "equal"),
        R("ffn_gate_exps.weight", "mlp.experts.gate_up_proj", [2048, 1024, 64], "Q4_K", "sliced"),
        R("ffn_up_exps.weight", "mlp.experts.gate_up_proj", [2048, 1024, 64], "Q4_K", "sliced"),
        R("ffn_down_exps.weight", "mlp.experts.down_proj", [1024, 2048, 64], "Q6_K", "quantized")
    ];

    /// <summary>The template's nodes in order as <c>id: label</c>; the frontend's explanation keys depend on the ids.</summary>
    public static readonly string[] TemplateNodes =
    [
        "input: Block input (residual stream)",
        "attn_norm: RMSNorm · attn_norm",
        "q: Query projection · attn_q",
        "k: Key projection · attn_k",
        "v: Value projection · attn_v",
        "q_norm: RMSNorm over the whole Q projection · attn_q_norm",
        "k_norm: RMSNorm over the whole K projection · attn_k_norm",
        "rope: Split into heads, then RoPE on Q and K",
        "attn: Attention over the KV cache",
        "mixer_out: Output projection · attn_output",
        "attn_residual: Add residual",
        "post_norm: RMSNorm · ffn_norm",
        "router: Router logits · ffn_gate_inp",
        "router_probs: Softmax over every expert",
        "topk: Top-k experts",
        "topk_weights: Chosen probabilities, used as is (not renormalized)",
        "exp_gate: Expert gate projection · ffn_gate_exps",
        "exp_up: Expert up projection · ffn_up_exps",
        "exp_act: SiLU(gate) × up, per expert",
        "exp_down: Expert down projection · ffn_down_exps",
        "exp_weighted: × router weight, summed over the chosen experts",
        "ffn_residual: Add residual",
        "control_vector: Add control vector (when one is loaded)",
        "output: Block output"
    ];

    /// <summary>The template's edges in order, as <c>from-&gt;to</c>.</summary>
    public static readonly string[] TemplateEdges =
    [
        "input->attn_norm", "attn_norm->q", "attn_norm->k", "attn_norm->v", "q->q_norm", "k->k_norm", "q_norm->rope", "k_norm->rope", "rope->attn",
        "v->attn", "attn->mixer_out", "mixer_out->attn_residual", "input->attn_residual", "attn_residual->post_norm", "post_norm->router",
        "router->router_probs", "router_probs->topk", "router_probs->topk_weights", "topk->topk_weights", "post_norm->exp_gate", "post_norm->exp_up",
        "topk->exp_gate", "topk->exp_up", "exp_gate->exp_act", "exp_up->exp_act", "exp_act->exp_down", "topk->exp_down", "exp_down->exp_weighted",
        "topk_weights->exp_weighted", "exp_weighted->ffn_residual", "attn_residual->ffn_residual", "ffn_residual->control_vector", "control_vector->output"
    ];

    /// <summary>16 MoE blocks, <c>token_embd</c>, <c>output_norm</c> and an untied <c>output</c>; 64 experts.</summary>
    /// <param name="fusedQkv">True stores each block's Q, K and V as one <c>attn_qkv</c> [2048, 6144] in place of <c>attn_q</c>, <c>attn_k</c> and <c>attn_v</c>.</param>
    public static GgufTensorInspection Olmoe1B7B(bool fusedQkv = false) =>
        Qwen35TensorFixture.Inspection("olmoe",
            new[]
                {
                    Qwen35TensorFixture.Tensor("token_embd.weight", [2048, 50_304], "Q4_K")
                }
                .Concat(Enumerable.Range(0, BlockCount).SelectMany(block => BlockTensors(block, fusedQkv)))
                .Concat([
                    Qwen35TensorFixture.Tensor("output_norm.weight", [2048], "F32"),
                    Qwen35TensorFixture.Tensor("output.weight", [2048, 50_304], "Q6_K")
                ]),
            expertCount: 64);

    private static IEnumerable<GgufTensorInfo> BlockTensors(int block, bool fusedQkv)
    {
        var rows = fusedQkv ? Block.Where(static row => row.GgufSuffix is not ("attn_q.weight" or "attn_k.weight" or "attn_v.weight")) : Block;
        var tensors = rows.Select(row => Qwen35TensorFixture.Tensor($"blk.{block}.{row.GgufSuffix}", row.Shape, row.GgmlType));
        return fusedQkv ? tensors.Prepend(Qwen35TensorFixture.Tensor($"blk.{block}.attn_qkv.weight", [2048, 6144], "Q4_K")) : tensors;
    }

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
