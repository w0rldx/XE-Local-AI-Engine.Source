namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Collections.Frozen;
using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using static ModelLabTemplateParts;

/// <summary>The static <c>llama</c> knowledge behind a detailed descriptor: HF tensor names and the dense full-attention template.</summary>
/// <remarks>
///     HF names follow <c>gguf-py/gguf/tensor_mapping.py</c> and <c>conversion/llama.py</c> (<c>LlamaModel.modify_tensors</c>); the
///     template follows <c>src/models/llama.cpp</c>, all at <see cref="TemplateLlamaCppTag" />. The names assume the standard
///     <c>LlamaForCausalLM</c> layout: Mistral's consolidated format, Llava and Voxtral language towers and other sources also
///     write <c>llama</c> from other names, and no metadata tells them apart.
/// </remarks>
internal sealed class ModelLabLlamaArchitecture : IModelLabArchitectureDescriptor
{
    // The tag the table and template were checked against. A test fails when the repo's llama.cpp pin moves past
    // it, so a bump re-reads the upstream files above before changing this.
    private const string TemplateLlamaCppTag = "b12014";

    private const string Source = "llama.cpp src/models/llama.cpp @ " + TemplateLlamaCppTag;
    private const string LayerPrefix = "model.layers.";
    private const string Equal = "equal";

    // Keyed by the name after "blk.N.". The graph's RoPE is NORM (adjacent pairs) while HF rotates half-split pairs,
    // so a working file holds Q and K, weights and biases, permuted against the standard layout on every converter path.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> BlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["attn_norm.weight"] = Hf("input_layernorm.weight", Equal),
        ["attn_q.weight"] = Hf("self_attn.q_proj.weight", ModelLabArchitectureBuilder.Reordered),
        ["attn_q.bias"] = Hf("self_attn.q_proj.bias", ModelLabArchitectureBuilder.Reordered),
        ["attn_k.weight"] = Hf("self_attn.k_proj.weight", ModelLabArchitectureBuilder.Reordered),
        ["attn_k.bias"] = Hf("self_attn.k_proj.bias", ModelLabArchitectureBuilder.Reordered),
        ["attn_v.weight"] = Hf("self_attn.v_proj.weight", Equal),
        ["attn_v.bias"] = Hf("self_attn.v_proj.bias", Equal),
        ["attn_output.weight"] = Hf("self_attn.o_proj.weight", Equal),
        ["attn_output.bias"] = Hf("self_attn.o_proj.bias", Equal),
        ["ffn_norm.weight"] = Hf("post_attention_layernorm.weight", Equal),
        ["ffn_gate.weight"] = Hf("mlp.gate_proj.weight", Equal),
        ["ffn_gate.bias"] = Hf("mlp.gate_proj.bias", Equal),
        ["ffn_up.weight"] = Hf("mlp.up_proj.weight", Equal),
        ["ffn_up.bias"] = Hf("mlp.up_proj.bias", Equal),
        ["ffn_down.weight"] = Hf("mlp.down_proj.weight", Equal),
        ["ffn_down.bias"] = Hf("mlp.down_proj.bias", Equal),
        // The router row follows MixtralForCausalLM naming: Mixtral-style MoE checkpoints also convert to llama.
        ["ffn_gate_inp.weight"] = Hf("block_sparse_moe.gate.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // A tied checkpoint (Llama 3.2 1B and 3B) has no output.weight. rope_freqs.weight (llama3 RoPE factors) has no HF tensor.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> NonBlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["token_embd.weight"] = Hf("model.embed_tokens.weight", Equal),
        ["output_norm.weight"] = Hf("model.norm.weight", Equal),
        ["output.weight"] = Hf("lm_head.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ModelLabArchitectureTemplate FullAttentionTemplate = BuildFullAttention();

    public string Architecture => "llama";

    public string LlamaCppTag => TemplateLlamaCppTag;

    public ModelLabHfTensor? BlockHfName(string ggufName, int blockIndex, GgufTensorInspection inspection)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"blk.{blockIndex}.");
        var suffix = ggufName.StartsWith(prefix, StringComparison.Ordinal) ? ggufName[prefix.Length..] : null;
        if (suffix is null || !BlockTensors.TryGetValue(suffix, out var entry))
        {
            return null;
        }

        return new ModelLabHfTensor
        {
            Name = string.Create(CultureInfo.InvariantCulture, $"{LayerPrefix}{blockIndex}.{entry.Name}"),
            Relation = entry.Relation
        };
    }

    public ModelLabHfTensor? NonBlockHfName(string ggufName) =>
        NonBlockTensors.GetValueOrDefault(ggufName);

    // The template draws the dense FFN only: a mixture-of-experts file (by metadata, which a later shard lacks, or by
    // its router and expert tensors) runs build_moe_ffn instead and gets none.
    public IReadOnlyList<ModelLabArchitectureTemplate> TemplatesFor(IReadOnlyList<ModelLabArchitectureBlock> blocks, GgufTensorInspection inspection)
    {
        var mixtureOfExperts = inspection.ExpertCount > 0
                               || blocks.SelectMany(static block => block.Groups).SelectMany(static group => group.Tensors).Any(static tensor => IsExpertTensor(tensor.GgufName));
        return !mixtureOfExperts && blocks.Any(static block => block.Kind == ModelLabArchitectureBuilder.FullAttentionKind) ? [FullAttentionTemplate] : [];
    }

    private static bool IsExpertTensor(string ggufName)
    {
        var stem = ModelLabArchitectureBuilder.Stem(ggufName);
        return stem == "ffn_gate_inp" || stem.EndsWith("_exps", StringComparison.Ordinal);
    }

    private static ModelLabHfTensor Hf(string name, string relation) =>
        new()
        {
            Name = name,
            Relation = relation
        };

    // graph: RMSNorm, Q/K/V (build_qkv also takes a fused attn_qkv), RoPE with the optional rope_freqs factors, attention,
    // dense SiLU FFN, then build_cvec. The Llama4-derived use_kq_norm branch (an L2 norm after RoPE) is not drawn.
    private static ModelLabArchitectureTemplate BuildFullAttention()
    {
        const string attention = ModelLabArchitectureBuilder.Attention;
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        Node(nodes, "input", "Block input (residual stream)", component: null);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "q", "Query projection · attn_q", attention);
        Node(nodes, "k", "Key projection · attn_k", attention);
        Node(nodes, "v", "Value projection · attn_v", attention);
        Node(nodes, "rope", "RoPE on Q and K", attention);
        Node(nodes, "attn", "Attention over the KV cache", attention);
        Node(nodes, "mixer_out", "Output projection · attn_output", attention);
        Node(nodes, "attn_residual", "Add residual", component: null);
        Node(nodes, "post_norm", "RMSNorm · ffn_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "ffn_gate", "Gate projection · ffn_gate", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_up", "Up projection · ffn_up", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_act", "SiLU(gate) × up", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_down", "Down projection · ffn_down", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_residual", "Add residual", component: null);
        Node(nodes, "control_vector", "Add control vector (when one is loaded)", component: null);
        Node(nodes, "output", "Block output", component: null);
        List<ModelLabArchitectureTemplateEdge> edges =
        [
            E("input", "attn_norm"), E("attn_norm", "q"), E("attn_norm", "k"), E("attn_norm", "v"), E("q", "rope"), E("k", "rope"),
            E("rope", "attn"), E("v", "attn"), E("attn", "mixer_out"), E("mixer_out", "attn_residual"), E("input", "attn_residual"),
            E("attn_residual", "post_norm"), E("post_norm", "ffn_gate"), E("post_norm", "ffn_up"), E("ffn_gate", "ffn_act"), E("ffn_up", "ffn_act"),
            E("ffn_act", "ffn_down"), E("ffn_down", "ffn_residual"), E("attn_residual", "ffn_residual"), E("ffn_residual", "control_vector"),
            E("control_vector", "output")
        ];
        return Template(ModelLabArchitectureBuilder.FullAttentionKind, Source, nodes, edges);
    }
}
