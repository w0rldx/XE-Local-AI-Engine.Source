namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Collections.Frozen;
using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using static ModelLabTemplateParts;

/// <summary>The static <c>qwen35</c> (Qwen3.5/3.8) knowledge behind a detailed descriptor: HF tensor names and one operation template per block kind.</summary>
/// <remarks>
///     HF names and value relations follow llama.cpp <c>gguf-py/gguf/tensor_mapping.py</c> and <c>conversion/qwen.py</c>
///     (<c>Qwen3NextModel.modify_tensors</c>); templates follow <c>src/models/qwen35.cpp</c> (<c>graph</c>,
///     <c>build_layer_attn</c>, <c>build_layer_attn_linear</c>, <c>graph_mtp</c>), all at <see cref="TemplateLlamaCppTag" />.
/// </remarks>
internal sealed class ModelLabQwen35Architecture : IModelLabArchitectureDescriptor
{
    // The tag the table and templates were checked against. A test fails when the repo's llama.cpp pin moves past
    // it, so a bump re-reads the upstream files above before changing this.
    private const string TemplateLlamaCppTag = "b12014";

    private const string Source = "llama.cpp src/models/qwen35.cpp @ " + TemplateLlamaCppTag;
    private const string LayerPrefix = "model.language_model.layers.";
    private const string Equal = "equal";
    private const string PlusOne = "plus_one";

    // Keyed by the name after "blk.N.". The converter adds 1 to every norm but linear_attn.norm and stores -exp(A_log).
    private static readonly FrozenDictionary<string, ModelLabHfTensor> BlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["attn_norm.weight"] = Hf("input_layernorm.weight", PlusOne),
        ["post_attention_norm.weight"] = Hf("post_attention_layernorm.weight", PlusOne),
        ["ssm_a"] = Hf("linear_attn.A_log", "neg_exp"),
        ["ssm_conv1d.weight"] = Hf("linear_attn.conv1d.weight", Equal),
        ["ssm_dt.bias"] = Hf("linear_attn.dt_bias", Equal),
        ["ssm_alpha.weight"] = Hf("linear_attn.in_proj_a.weight", Equal),
        ["ssm_beta.weight"] = Hf("linear_attn.in_proj_b.weight", Equal),
        ["attn_qkv.weight"] = Hf("linear_attn.in_proj_qkv.weight", Equal),
        ["attn_gate.weight"] = Hf("linear_attn.in_proj_z.weight", Equal),
        ["ssm_norm.weight"] = Hf("linear_attn.norm.weight", Equal),
        ["ssm_out.weight"] = Hf("linear_attn.out_proj.weight", Equal),
        ["attn_q.weight"] = Hf("self_attn.q_proj.weight", Equal),
        ["attn_k.weight"] = Hf("self_attn.k_proj.weight", Equal),
        ["attn_v.weight"] = Hf("self_attn.v_proj.weight", Equal),
        ["attn_output.weight"] = Hf("self_attn.o_proj.weight", Equal),
        ["attn_q_norm.weight"] = Hf("self_attn.q_norm.weight", PlusOne),
        ["attn_k_norm.weight"] = Hf("self_attn.k_norm.weight", PlusOne),
        ["ffn_gate.weight"] = Hf("mlp.gate_proj.weight", Equal),
        ["ffn_up.weight"] = Hf("mlp.up_proj.weight", Equal),
        ["ffn_down.weight"] = Hf("mlp.down_proj.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // The linear_attn tensors _LinearAttentionVReorderBase.modify_tensors permutes when the key and value head counts
    // differ: the V rows of in_proj_qkv, in_proj_z/a/b, A_log, dt_bias, the V channels of conv1d, out_proj's columns.
    private static readonly FrozenSet<string> ValueHeadReordered = new[]
    {
        "attn_qkv.weight",
        "attn_gate.weight",
        "ssm_alpha.weight",
        "ssm_beta.weight",
        "ssm_a",
        "ssm_dt.bias",
        "ssm_conv1d.weight",
        "ssm_out.weight"
    }.ToFrozenSet(StringComparer.Ordinal);

    // A tied checkpoint (the 0.8B) has no output.weight; lm_head.weight is the head name the converter keeps.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> NonBlockTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["token_embd.weight"] = Hf("model.language_model.embed_tokens.weight", Equal),
        ["output_norm.weight"] = Hf("model.language_model.norm.weight", PlusOne),
        ["output.weight"] = Hf("lm_head.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ModelLabArchitectureTemplate LinearAttentionTemplate = BuildLinearAttention();
    private static readonly ModelLabArchitectureTemplate FullAttentionTemplate = BuildFullAttention();
    private static readonly ModelLabArchitectureTemplate MtpTemplate = BuildMtp();

    public string Architecture => "qwen35";

    public string LlamaCppTag => TemplateLlamaCppTag;

    public ModelLabHfTensor? BlockHfName(string ggufName, int blockIndex, GgufTensorInspection inspection)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"blk.{blockIndex}.");
        var suffix = ggufName.StartsWith(prefix, StringComparison.Ordinal) ? ggufName[prefix.Length..] : null;
        if (suffix is null || !BlockTensors.TryGetValue(suffix, out var entry))
        {
            return null;
        }

        // qwen35.cpp: n_k_heads = ssm_n_group (ssm.group_count), n_v_heads = ssm_dt_rank (ssm.time_step_rank).
        bool? valueHeadsDiffer = inspection is { SsmGroupCount: { } keyHeads, SsmTimeStepRank: { } valueHeads } ? keyHeads != valueHeads : null;
        var relation = entry.Relation;
        if (ValueHeadReordered.Contains(suffix) && valueHeadsDiffer != false)
        {
            relation = valueHeadsDiffer == true ? ModelLabArchitectureBuilder.Reordered : null;
        }

        return new ModelLabHfTensor
        {
            Name = string.Create(CultureInfo.InvariantCulture, $"{LayerPrefix}{blockIndex}.{entry.Name}"),
            Relation = relation
        };
    }

    public ModelLabHfTensor? NonBlockHfName(string ggufName) =>
        NonBlockTensors.GetValueOrDefault(ggufName);

    // One template per distinct block kind present, in the order linear attention, full attention, MTP.
    public IReadOnlyList<ModelLabArchitectureTemplate> TemplatesFor(IReadOnlyList<ModelLabArchitectureBlock> blocks, GgufTensorInspection inspection)
    {
        var present = blocks.Select(static block => block.Kind).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. new[]
            {
                LinearAttentionTemplate,
                FullAttentionTemplate,
                MtpTemplate
            }.Where(template => present.Contains(template.Kind))
        ];
    }

    private static ModelLabHfTensor Hf(string name, string relation) =>
        new()
        {
            Name = name,
            Relation = relation
        };

    // graph::build_layer_attn_linear: projections, causal conv, L2-normed Q and K into the gated delta rule, gated norm.
    private static ModelLabArchitectureTemplate BuildLinearAttention()
    {
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        var edges = new List<ModelLabArchitectureTemplateEdge>();
        Node(nodes, "input", "Block input (residual stream)", component: null);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "qkv", "QKV projection · attn_qkv", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "z", "Output-gate projection · attn_gate", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "beta", "β = sigmoid(ssm_beta · x)", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "alpha", "α projection · ssm_alpha", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "decay", "Decay gate: softplus(α + ssm_dt) × ssm_a", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "conv", "Causal conv1d · ssm_conv1d (conv state), then SiLU", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "split", "Split into Q, K, V", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "l2_norm", "L2 norm of Q and K", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "delta", "Gated delta rule (recurrent state)", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "gated_norm", "RMSNorm · ssm_norm, × SiLU(z)", ModelLabArchitectureBuilder.Ssm);
        Node(nodes, "mixer_out", "Output projection · ssm_out", ModelLabArchitectureBuilder.Ssm);
        edges.AddRange([
            E("input", "attn_norm"), E("attn_norm", "qkv"), E("attn_norm", "z"), E("attn_norm", "beta"), E("attn_norm", "alpha"),
            E("alpha", "decay"), E("qkv", "conv"), E("conv", "split"), E("split", "l2_norm"), E("split", "delta"), E("l2_norm", "delta"),
            E("decay", "delta"), E("beta", "delta"), E("delta", "gated_norm"), E("z", "gated_norm"), E("gated_norm", "mixer_out")
        ]);
        AddTail(nodes, edges, residualFrom: "input", trunk: true);
        return Template(ModelLabArchitectureBuilder.LinearAttentionKind, Source, nodes, edges);
    }

    // graph::build_layer_attn: Q carries a gate half, Q and K are RMS-normed, then MRoPE, attention and sigmoid(gate).
    private static ModelLabArchitectureTemplate BuildFullAttention()
    {
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        var edges = new List<ModelLabArchitectureTemplateEdge>();
        Node(nodes, "input", "Block input (residual stream)", component: null);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        edges.Add(E("input", "attn_norm"));
        AddGatedAttention(nodes, edges);
        AddTail(nodes, edges, residualFrom: "input", trunk: true);
        return Template(ModelLabArchitectureBuilder.FullAttentionKind, Source, nodes, edges);
    }

    // graph_mtp: the normed trunk state and the token embedding are joined by eh_proj, run one full-attention block
    // and a head of their own. It runs as a separate draft graph, never inside the main forward pass.
    private static ModelLabArchitectureTemplate BuildMtp()
    {
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        var edges = new List<ModelLabArchitectureTemplateEdge>();
        Node(nodes, "hidden", "Trunk hidden state after output_norm", component: null);
        Node(nodes, "token", "Token embedding · nextn.embed_tokens, else token_embd", component: null);
        Node(nodes, "hnorm", "RMSNorm · nextn.hnorm", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "enorm", "RMSNorm · nextn.enorm", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "concat", "Concat [embedding, hidden]", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "eh_proj", "Projection · nextn.eh_proj", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        edges.AddRange([
            E("hidden", "hnorm"), E("token", "enorm"), E("enorm", "concat"), E("hnorm", "concat"), E("concat", "eh_proj"), E("eh_proj", "attn_norm")
        ]);
        AddGatedAttention(nodes, edges);
        AddTail(nodes, edges, residualFrom: "eh_proj", trunk: false);
        Node(nodes, "head_norm", "RMSNorm · nextn.shared_head_norm, else output_norm", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "head", "LM head · nextn.shared_head_head, else output", component: null);
        Node(nodes, "logits", "Draft-token logits", component: null);
        edges.AddRange([E("ffn_residual", "head_norm"), E("head_norm", "head"), E("head", "logits")]);
        return Template(ModelLabArchitectureBuilder.MtpKind, Source, nodes, edges);
    }

    private static void AddGatedAttention(List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges)
    {
        const string attention = ModelLabArchitectureBuilder.Attention;
        Node(nodes, "q", "Query + gate projection · attn_q", attention);
        Node(nodes, "k", "Key projection · attn_k", attention);
        Node(nodes, "v", "Value projection · attn_v", attention);
        Node(nodes, "q_norm", "RMSNorm · attn_q_norm", attention);
        Node(nodes, "k_norm", "RMSNorm · attn_k_norm", attention);
        Node(nodes, "rope", "MRoPE on Q and K", attention);
        Node(nodes, "attn", "Attention over the KV cache", attention);
        Node(nodes, "gate", "sigmoid(gate half of attn_q)", attention);
        Node(nodes, "gated", "Attention × gate", attention);
        Node(nodes, "mixer_out", "Output projection · attn_output", attention);
        edges.AddRange([
            E("attn_norm", "q"), E("attn_norm", "k"), E("attn_norm", "v"), E("q", "q_norm"), E("k", "k_norm"), E("q_norm", "rope"),
            E("k_norm", "rope"), E("rope", "attn"), E("v", "attn"), E("q", "gate"), E("attn", "gated"), E("gate", "gated"), E("gated", "mixer_out")
        ]);
    }

    // Both residual adds and the dense SiLU FFN (build_layer_ffn); a trunk block then adds a loaded control vector.
    private static void AddTail(List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges, string residualFrom, bool trunk)
    {
        Node(nodes, "attn_residual", "Add residual", component: null);
        Node(nodes, "post_norm", "RMSNorm · post_attention_norm", ModelLabArchitectureBuilder.Norm);
        Node(nodes, "ffn_gate", "Gate projection · ffn_gate", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_up", "Up projection · ffn_up", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_act", "SiLU(gate) × up", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_down", "Down projection · ffn_down", ModelLabArchitectureBuilder.Ffn);
        Node(nodes, "ffn_residual", "Add residual", component: null);
        edges.AddRange([
            E("mixer_out", "attn_residual"), E(residualFrom, "attn_residual"), E("attn_residual", "post_norm"), E("post_norm", "ffn_gate"),
            E("post_norm", "ffn_up"), E("ffn_gate", "ffn_act"), E("ffn_up", "ffn_act"), E("ffn_act", "ffn_down"), E("ffn_down", "ffn_residual"),
            E("attn_residual", "ffn_residual")
        ]);
        if (trunk)
        {
            Node(nodes, "control_vector", "Add control vector (when one is loaded)", component: null);
            Node(nodes, "output", "Block output", component: null);
            edges.AddRange([E("ffn_residual", "control_vector"), E("control_vector", "output")]);
        }
    }
}
