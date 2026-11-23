namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Collections.Frozen;
using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using static ModelLabTemplateParts;

/// <summary>The static <c>olmoe</c> knowledge behind a detailed descriptor: HF tensor names and the mixture-of-experts full-attention template.</summary>
/// <remarks>
///     HF names are the transformers in-memory ones (<c>OlmoeForCausalLM</c>), which fuse the per-expert checkpoint tensors on load;
///     relations follow <c>conversion/olmo.py</c> (<c>OlmoeModel</c>). The template follows <c>src/models/olmoe.cpp</c> and
///     <c>src/llama-graph.cpp</c> (<c>build_moe_ffn</c>), all at <see cref="TemplateLlamaCppTag" />.
/// </remarks>
internal sealed class ModelLabOlmoeArchitecture : IModelLabArchitectureDescriptor
{
    // The tag the table and template were checked against. A test fails when the repo's llama.cpp pin moves past
    // it, so a bump re-reads the upstream files above before changing this.
    private const string TemplateLlamaCppTag = "b12014";

    private const string Source = "llama.cpp src/models/olmoe.cpp @ " + TemplateLlamaCppTag;
    private const string LayerPrefix = "model.layers.";
    private const string Equal = "equal";

    // Keyed by the name after "blk.N.". NEOX RoPE rotates half-split pairs like HF, so Q and K pass through unpermuted.
    // Experts: transformers holds gate_up_proj [E, 2F, H] with gate as the first F rows; llama.cpp stores the halves.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> BlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["attn_norm.weight"] = Hf("input_layernorm.weight", Equal),
        ["attn_q.weight"] = Hf("self_attn.q_proj.weight", Equal),
        ["attn_q.bias"] = Hf("self_attn.q_proj.bias", Equal),
        ["attn_k.weight"] = Hf("self_attn.k_proj.weight", Equal),
        ["attn_k.bias"] = Hf("self_attn.k_proj.bias", Equal),
        ["attn_v.weight"] = Hf("self_attn.v_proj.weight", Equal),
        ["attn_v.bias"] = Hf("self_attn.v_proj.bias", Equal),
        ["attn_output.weight"] = Hf("self_attn.o_proj.weight", Equal),
        ["attn_q_norm.weight"] = Hf("self_attn.q_norm.weight", Equal),
        ["attn_k_norm.weight"] = Hf("self_attn.k_norm.weight", Equal),
        ["ffn_norm.weight"] = Hf("post_attention_layernorm.weight", Equal),
        ["ffn_gate_inp.weight"] = Hf("mlp.gate.weight", Equal),
        ["ffn_gate_exps.weight"] = Hf("mlp.experts.gate_up_proj", ModelLabArchitectureBuilder.Sliced),
        ["ffn_up_exps.weight"] = Hf("mlp.experts.gate_up_proj", ModelLabArchitectureBuilder.Sliced),
        ["ffn_down_exps.weight"] = Hf("mlp.experts.down_proj", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // OLMoE checkpoints are untied: output.weight is required.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> NonBlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["token_embd.weight"] = Hf("model.embed_tokens.weight", Equal),
        ["output_norm.weight"] = Hf("model.norm.weight", Equal),
        ["output.weight"] = Hf("lm_head.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ModelLabArchitectureTemplate FullAttentionTemplate =
        BuildFullAttention("Query projection · attn_q", "Key projection · attn_k", "Value projection · attn_v");

    // build_qkv projects once with a fused attn_qkv and splits the result: same graph, only the Q/K/V labels differ.
    private static readonly ModelLabArchitectureTemplate FusedQkvTemplate = BuildFullAttention("Query rows of the fused projection · attn_qkv",
        "Key rows of the fused projection · attn_qkv", "Value rows of the fused projection · attn_qkv");

    // llama.cpp picks fused or separate per layer, so a file mixing both gets labels true for either.
    private static readonly ModelLabArchitectureTemplate MixedQkvTemplate = BuildFullAttention("Query projection · attn_q or Q rows of attn_qkv",
        "Key projection · attn_k or K rows of attn_qkv", "Value projection · attn_v or V rows of attn_qkv");

    public string Architecture => "olmoe";

    public string LlamaCppTag => TemplateLlamaCppTag;

    public ModelLabHfTensor? BlockHfName(string ggufName, int blockIndex, string blockKind, GgufTensorInspection inspection)
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

    public IReadOnlyList<ModelLabArchitectureTemplate> TemplatesFor(IReadOnlyList<ModelLabArchitectureBlock> blocks, GgufTensorInspection inspection)
    {
        var full = blocks.Where(static block => block.Kind == ModelLabArchitectureBuilder.FullAttentionKind).ToList();
        if (full.Count == 0)
        {
            return [];
        }

        // By the weight: a fused attn_qkv.weight may sit beside separate attn_q/k/v biases (create_tensor_qkv).
        var fusedCount = full.Count(static block => block.Groups.SelectMany(static group => group.Tensors).Any(static tensor => tensor.GgufName.EndsWith(".attn_qkv.weight", StringComparison.Ordinal)));
        var template = fusedCount switch
        {
            0 => FullAttentionTemplate,
            _ when fusedCount == full.Count => FusedQkvTemplate,
            _ => MixedQkvTemplate
        };
        return [template];
    }

    private static ModelLabHfTensor Hf(string name, string relation) =>
        new()
        {
            Name = name,
            Relation = relation
        };

    // graph: RMSNorm, Q/K/V, RMSNorm of the whole Q and K projections, head reshape and RoPE, attention, then build_moe_ffn
    // (softmax gating, norm_w false, weight scale 0 so unused, no shared expert) and build_cvec. No clip_qkv clamp is applied.
    private static ModelLabArchitectureTemplate BuildFullAttention(string queryLabel, string keyLabel, string valueLabel)
    {
        const string attention = ModelLabArchitectureBuilder.Attention;
        const string ffn = ModelLabArchitectureBuilder.Ffn;
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        Node(nodes, "input", "Block input (residual stream)", component: null);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "q", queryLabel, attention);
        Node(nodes, "k", keyLabel, attention);
        Node(nodes, "v", valueLabel, attention);
        Node(nodes, "q_norm", "RMSNorm over the whole Q projection · attn_q_norm", attention);
        Node(nodes, "k_norm", "RMSNorm over the whole K projection · attn_k_norm", attention);
        Node(nodes, "rope", "Split into heads, then RoPE on Q and K", attention);
        Node(nodes, "attn", "Attention over the KV cache", attention);
        Node(nodes, "mixer_out", "Output projection · attn_output", attention);
        Node(nodes, "attn_residual", "Add residual", component: null);
        Node(nodes, "post_norm", "RMSNorm · ffn_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "router", "Router logits · ffn_gate_inp", ffn);
        Node(nodes, "router_probs", "Softmax over every expert", ffn);
        Node(nodes, "topk", "Top-k experts", ffn);
        Node(nodes, "topk_weights", "Chosen probabilities, used as is (not renormalized)", ffn);
        Node(nodes, "exp_gate", "Expert gate projection · ffn_gate_exps", ffn);
        Node(nodes, "exp_up", "Expert up projection · ffn_up_exps", ffn);
        Node(nodes, "exp_act", "SiLU(gate) × up, per expert", ffn);
        Node(nodes, "exp_down", "Expert down projection · ffn_down_exps", ffn);
        Node(nodes, "exp_weighted", "× router weight, summed over the chosen experts", ffn);
        Node(nodes, "ffn_residual", "Add residual", component: null);
        Node(nodes, "control_vector", "Add control vector (when one is loaded)", component: null);
        Node(nodes, "output", "Block output", component: null);
        List<ModelLabArchitectureTemplateEdge> edges =
        [
            E("input", "attn_norm"), E("attn_norm", "q"), E("attn_norm", "k"), E("attn_norm", "v"), E("q", "q_norm"), E("k", "k_norm"),
            E("q_norm", "rope"), E("k_norm", "rope"), E("rope", "attn"), E("v", "attn"), E("attn", "mixer_out"), E("mixer_out", "attn_residual"),
            E("input", "attn_residual"), E("attn_residual", "post_norm"), E("post_norm", "router"), E("router", "router_probs"),
            E("router_probs", "topk"), E("router_probs", "topk_weights"), E("topk", "topk_weights"), E("post_norm", "exp_gate"), E("post_norm", "exp_up"),
            E("topk", "exp_gate"), E("topk", "exp_up"), E("exp_gate", "exp_act"), E("exp_up", "exp_act"), E("exp_act", "exp_down"), E("topk", "exp_down"),
            E("exp_down", "exp_weighted"), E("topk_weights", "exp_weighted"), E("exp_weighted", "ffn_residual"), E("attn_residual", "ffn_residual"),
            E("ffn_residual", "control_vector"), E("control_vector", "output")
        ];
        return Template(ModelLabArchitectureBuilder.FullAttentionKind, Source, nodes, edges);
    }
}
