namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Collections.Frozen;
using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using static ModelLabTemplateParts;

/// <summary>The static <c>qwen35</c> (Qwen3.5/3.8) or <c>qwen35moe</c> knowledge behind a detailed descriptor: HF tensor names and one operation template per block kind.</summary>
/// <remarks>
///     HF names and value relations follow llama.cpp <c>gguf-py/gguf/tensor_mapping.py</c> and <c>conversion/qwen.py</c>
///     (<c>Qwen3NextModel</c>, <c>Qwen2MoeModel.modify_tensors</c>); templates follow <c>src/models/qwen35.cpp</c> or
///     <c>qwen35moe.cpp</c> (<c>graph</c>, <c>build_layer_attn</c>, <c>build_layer_attn_linear</c>, <c>build_layer_ffn</c>,
///     <c>graph_mtp</c>) and <c>src/llama-graph.cpp</c> (<c>build_moe_ffn</c>), all at <see cref="TemplateLlamaCppTag" />.
/// </remarks>
internal sealed class ModelLabQwen35Architecture : IModelLabArchitectureDescriptor
{
    // The tag the table and templates were checked against. A test fails when the repo's llama.cpp pin moves past
    // it, so a bump re-reads the upstream files above before changing this.
    private const string TemplateLlamaCppTag = "b12014";

    private const string LayerPrefix = "model.language_model.layers.";
    private const string Equal = "equal";
    private const string PlusOne = "plus_one";

    // The Q part of a gated block is 2·n_head·d rows, each head's query then its gate; a fused attn_qkv starts with it.
    private const string Query = "Query + gate projection · attn_q";
    private const string Gate = "sigmoid(gate half of attn_q)";
    private const string FusedQuery = "Query + gate rows of the fused projection · attn_qkv";
    private const string FusedGate = "sigmoid(gate half of the Q rows of attn_qkv)";
    private const string MixedQuery = "Query + gate projection · attn_q or Q + gate rows of attn_qkv";
    private const string MixedGate = "sigmoid(gate half of attn_q or of the Q rows of attn_qkv)";

    // Keyed by the name after "blk.N.", shared by both families; each adds its FFN rows below. The converter adds 1 to
    // every norm but linear_attn.norm and stores -exp(A_log).
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
        ["attn_k_norm.weight"] = Hf("self_attn.k_norm.weight", PlusOne)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ModelLabHfTensor> DenseFfnTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["ffn_gate.weight"] = Hf("mlp.gate_proj.weight", Equal),
        ["ffn_up.weight"] = Hf("mlp.up_proj.weight", Equal),
        ["ffn_down.weight"] = Hf("mlp.down_proj.weight", Equal)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // The converter splits the fused HF gate_up_proj (first half of its rows → gate); --fuse-gate-up-exps keeps it whole,
    // in HF order. A per-expert checkpoint (mlp.experts.N.*) is stacked instead, and no metadata tells the two apart.
    private static readonly FrozenDictionary<string, ModelLabHfTensor> ExpertFfnTensors = new Dictionary<string, ModelLabHfTensor>(StringComparer.Ordinal)
    {
        ["ffn_gate_inp.weight"] = Hf("mlp.gate.weight", Equal),
        ["ffn_gate_exps.weight"] = Hf("mlp.experts.gate_up_proj", ModelLabArchitectureBuilder.Sliced),
        ["ffn_up_exps.weight"] = Hf("mlp.experts.gate_up_proj", ModelLabArchitectureBuilder.Sliced),
        ["ffn_gate_up_exps.weight"] = Hf("mlp.experts.gate_up_proj", Equal),
        ["ffn_down_exps.weight"] = Hf("mlp.experts.down_proj", Equal),
        // HF [1, hidden]; a requantized file stores [hidden] (gguf writes ggml_n_dims, dropping the trailing 1).
        ["ffn_gate_inp_shexp.weight"] = Hf("mlp.shared_expert_gate.weight", Equal),
        ["ffn_gate_shexp.weight"] = Hf("mlp.shared_expert.gate_proj.weight", Equal),
        ["ffn_up_shexp.weight"] = Hf("mlp.shared_expert.up_proj.weight", Equal),
        ["ffn_down_shexp.weight"] = Hf("mlp.shared_expert.down_proj.weight", Equal)
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

    private readonly FrozenDictionary<string, ModelLabHfTensor> _ffnTensors;

    private readonly ModelLabArchitectureTemplate _linearAttention;
    private readonly ModelLabArchitectureTemplate _fullAttention;
    private readonly ModelLabArchitectureTemplate _fusedFullAttention;
    private readonly ModelLabArchitectureTemplate _mixedFullAttention;
    private readonly ModelLabArchitectureTemplate _mtp;
    private readonly ModelLabArchitectureTemplate _fusedMtp;
    private readonly ModelLabArchitectureTemplate _mixedMtp;

    /// <param name="mixtureOfExperts">True for <c>qwen35moe</c>: routed experts plus a gated shared expert in place of the dense FFN.</param>
    public ModelLabQwen35Architecture(bool mixtureOfExperts)
    {
        Architecture = mixtureOfExperts ? "qwen35moe" : "qwen35";
        _ffnTensors = mixtureOfExperts ? ExpertFfnTensors : DenseFfnTensors;
        var source = "llama.cpp src/models/" + Architecture + ".cpp @ " + TemplateLlamaCppTag;
        _linearAttention = BuildLinearAttention(source, mixtureOfExperts);
        _fullAttention = BuildFullAttention(source, mixtureOfExperts, Query, ModelLabQkvLabels.Key, ModelLabQkvLabels.Value, Gate);
        _fusedFullAttention = BuildFullAttention(source, mixtureOfExperts, FusedQuery, ModelLabQkvLabels.FusedKey, ModelLabQkvLabels.FusedValue, FusedGate);
        _mixedFullAttention = BuildFullAttention(source, mixtureOfExperts, MixedQuery, ModelLabQkvLabels.MixedKey, ModelLabQkvLabels.MixedValue, MixedGate);
        _mtp = BuildMtp(source, mixtureOfExperts, Query, ModelLabQkvLabels.Key, ModelLabQkvLabels.Value, Gate);
        _fusedMtp = BuildMtp(source, mixtureOfExperts, FusedQuery, ModelLabQkvLabels.FusedKey, ModelLabQkvLabels.FusedValue, FusedGate);
        _mixedMtp = BuildMtp(source, mixtureOfExperts, MixedQuery, ModelLabQkvLabels.MixedKey, ModelLabQkvLabels.MixedValue, MixedGate);
    }

    public string Architecture { get; }

    public string LlamaCppTag => TemplateLlamaCppTag;

    public ModelLabHfTensor? BlockHfName(string ggufName, int blockIndex, string blockKind, GgufTensorInspection inspection)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"blk.{blockIndex}.");
        var suffix = ggufName.StartsWith(prefix, StringComparison.Ordinal) ? ggufName[prefix.Length..] : null;
        // Outside a linear block attn_qkv is q_proj, k_proj and v_proj fused (create_tensor_qkv): no one HF tensor to name.
        if (suffix is null || (suffix == "attn_qkv.weight" && blockKind != ModelLabArchitectureBuilder.LinearAttentionKind)
                           || !(BlockTensors.TryGetValue(suffix, out var entry) || _ffnTensors.TryGetValue(suffix, out entry)))
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

    // One template per distinct block kind present, in the order linear attention, full attention, MTP. Full-attention and MTP
    // blocks pick their Q/K/V labels apart; a linear block's attn_qkv is the delta-net projection and never counts.
    public IReadOnlyList<ModelLabArchitectureTemplate> TemplatesFor(IReadOnlyList<ModelLabArchitectureBlock> blocks, GgufTensorInspection inspection)
    {
        ModelLabArchitectureTemplate?[] templates =
        [
            blocks.Any(static block => block.Kind == ModelLabArchitectureBuilder.LinearAttentionKind) ? _linearAttention : null,
            ModelLabQkvLabels.ByLayout(blocks, ModelLabArchitectureBuilder.FullAttentionKind, _fullAttention, _fusedFullAttention, _mixedFullAttention),
            ModelLabQkvLabels.ByLayout(blocks, ModelLabArchitectureBuilder.MtpKind, _mtp, _fusedMtp, _mixedMtp)
        ];
        return [.. templates.OfType<ModelLabArchitectureTemplate>()];
    }

    private static ModelLabHfTensor Hf(string name, string relation) =>
        new()
        {
            Name = name,
            Relation = relation
        };

    // graph::build_layer_attn_linear: projections, causal conv, L2-normed Q and K into the gated delta rule, gated norm.
    private static ModelLabArchitectureTemplate BuildLinearAttention(string source, bool mixtureOfExperts)
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
        AddTail(nodes, edges, residualFrom: "input", trunk: true, mixtureOfExperts);
        return Template(ModelLabArchitectureBuilder.LinearAttentionKind, source, nodes, edges);
    }

    // graph::build_layer_attn: Q carries a gate half, Q and K are RMS-normed, then MRoPE, attention and sigmoid(gate).
    private static ModelLabArchitectureTemplate BuildFullAttention(string source, bool mixtureOfExperts, string queryLabel, string keyLabel, string valueLabel,
        string gateLabel)
    {
        var nodes = new List<ModelLabArchitectureTemplateNode>();
        var edges = new List<ModelLabArchitectureTemplateEdge>();
        Node(nodes, "input", "Block input (residual stream)", component: null);
        Node(nodes, "attn_norm", "RMSNorm · attn_norm", ModelLabArchitectureBuilder.Norm);
        edges.Add(E("input", "attn_norm"));
        AddGatedAttention(nodes, edges, queryLabel, keyLabel, valueLabel, gateLabel);
        AddTail(nodes, edges, residualFrom: "input", trunk: true, mixtureOfExperts);
        return Template(ModelLabArchitectureBuilder.FullAttentionKind, source, nodes, edges);
    }

    // graph_mtp: the normed trunk state and the token embedding are joined by eh_proj, run one full-attention block
    // and a head of their own. It runs as a separate draft graph, never inside the main forward pass.
    private static ModelLabArchitectureTemplate BuildMtp(string source, bool mixtureOfExperts, string queryLabel, string keyLabel, string valueLabel, string gateLabel)
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
        AddGatedAttention(nodes, edges, queryLabel, keyLabel, valueLabel, gateLabel);
        AddTail(nodes, edges, residualFrom: "eh_proj", trunk: false, mixtureOfExperts);
        Node(nodes, "head_norm", "RMSNorm · nextn.shared_head_norm, else output_norm", ModelLabArchitectureBuilder.Mtp);
        Node(nodes, "head", "LM head · nextn.shared_head_head, else output", component: null);
        Node(nodes, "logits", "Draft-token logits", component: null);
        edges.AddRange([E("ffn_residual", "head_norm"), E("head_norm", "head"), E("head", "logits")]);
        return Template(ModelLabArchitectureBuilder.MtpKind, source, nodes, edges);
    }

    private static void AddGatedAttention(List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges, string queryLabel, string keyLabel,
        string valueLabel, string gateLabel)
    {
        const string attention = ModelLabArchitectureBuilder.Attention;
        Node(nodes, "q", queryLabel, attention);
        Node(nodes, "k", keyLabel, attention);
        Node(nodes, "v", valueLabel, attention);
        Node(nodes, "q_norm", "RMSNorm · attn_q_norm", attention);
        Node(nodes, "k_norm", "RMSNorm · attn_k_norm", attention);
        Node(nodes, "rope", "MRoPE on Q and K", attention);
        Node(nodes, "attn", "Attention over the KV cache", attention);
        Node(nodes, "gate", gateLabel, attention);
        Node(nodes, "gated", "Attention × gate", attention);
        Node(nodes, "mixer_out", "Output projection · attn_output", attention);
        edges.AddRange([
            E("attn_norm", "q"), E("attn_norm", "k"), E("attn_norm", "v"), E("q", "q_norm"), E("k", "k_norm"), E("q_norm", "rope"),
            E("k_norm", "rope"), E("rope", "attn"), E("v", "attn"), E("q", "gate"), E("attn", "gated"), E("gate", "gated"), E("gated", "mixer_out")
        ]);
    }

    // Both residual adds and the SiLU FFN (build_layer_ffn), dense or mixture of experts; a trunk block then adds a
    // loaded control vector.
    private static void AddTail(List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges, string residualFrom, bool trunk,
        bool mixtureOfExperts)
    {
        Node(nodes, "attn_residual", "Add residual", component: null);
        Node(nodes, "post_norm", "RMSNorm · post_attention_norm", ModelLabArchitectureBuilder.Norm);
        edges.AddRange([E("mixer_out", "attn_residual"), E(residualFrom, "attn_residual"), E("attn_residual", "post_norm")]);
        if (mixtureOfExperts)
        {
            AddMixtureOfExperts(nodes, edges);
        }
        else
        {
            Node(nodes, "ffn_gate", "Gate projection · ffn_gate", ModelLabArchitectureBuilder.Ffn);
            Node(nodes, "ffn_up", "Up projection · ffn_up", ModelLabArchitectureBuilder.Ffn);
            Node(nodes, "ffn_act", "SiLU(gate) × up", ModelLabArchitectureBuilder.Ffn);
            Node(nodes, "ffn_down", "Down projection · ffn_down", ModelLabArchitectureBuilder.Ffn);
            edges.AddRange([E("post_norm", "ffn_gate"), E("post_norm", "ffn_up"), E("ffn_gate", "ffn_act"), E("ffn_up", "ffn_act"), E("ffn_act", "ffn_down")]);
        }

        Node(nodes, "ffn_residual", "Add residual", component: null);
        edges.AddRange([E(mixtureOfExperts ? "moe_out" : "ffn_down", "ffn_residual"), E("attn_residual", "ffn_residual")]);
        if (trunk)
        {
            Node(nodes, "control_vector", "Add control vector (when one is loaded)", component: null);
            Node(nodes, "output", "Block output", component: null);
            edges.AddRange([E("ffn_residual", "control_vector"), E("control_vector", "output")]);
        }
    }

    // build_moe_ffn (softmax gating, norm_w, weighting after down) plus the sigmoid-gated shared expert. Not drawn: the
    // fused gate_up path (same operations), the weight scale (0, unused), the unset SwiGLU clamp, the expert-cache slots.
    private static void AddMixtureOfExperts(List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges)
    {
        const string ffn = ModelLabArchitectureBuilder.Ffn;
        Node(nodes, "router", "Router logits · ffn_gate_inp", ffn);
        Node(nodes, "router_probs", "Softmax over every expert", ffn);
        Node(nodes, "topk", "Top-k experts", ffn);
        Node(nodes, "topk_weights", "Chosen probabilities ÷ their sum", ffn);
        Node(nodes, "exp_gate", "Expert gate projection · ffn_gate_exps (logical half when the file stores ffn_gate_up_exps)", ffn);
        Node(nodes, "exp_up", "Expert up projection · ffn_up_exps (logical half when the file stores ffn_gate_up_exps)", ffn);
        Node(nodes, "exp_act", "SiLU(gate) × up, per expert", ffn);
        Node(nodes, "exp_down", "Expert down projection · ffn_down_exps", ffn);
        Node(nodes, "exp_weighted", "× router weight, summed over the chosen experts", ffn);
        Node(nodes, "shexp_gate", "Shared-expert gate projection · ffn_gate_shexp", ffn);
        Node(nodes, "shexp_up", "Shared-expert up projection · ffn_up_shexp", ffn);
        Node(nodes, "shexp_act", "SiLU(gate) × up", ffn);
        Node(nodes, "shexp_down", "Shared-expert down projection · ffn_down_shexp", ffn);
        Node(nodes, "shexp_scale", "sigmoid(ffn_gate_inp_shexp · x)", ffn);
        Node(nodes, "shexp_gated", "Shared expert × its gate", ffn);
        Node(nodes, "moe_out", "Add routed and shared experts", ffn);
        edges.AddRange([
            E("post_norm", "router"), E("router", "router_probs"), E("router_probs", "topk"), E("router_probs", "topk_weights"), E("topk", "topk_weights"),
            E("post_norm", "exp_gate"), E("post_norm", "exp_up"), E("topk", "exp_gate"), E("topk", "exp_up"), E("exp_gate", "exp_act"), E("exp_up", "exp_act"),
            E("exp_act", "exp_down"), E("topk", "exp_down"), E("exp_down", "exp_weighted"), E("topk_weights", "exp_weighted"), E("post_norm", "shexp_gate"),
            E("post_norm", "shexp_up"), E("shexp_gate", "shexp_act"), E("shexp_up", "shexp_act"), E("shexp_act", "shexp_down"), E("post_norm", "shexp_scale"),
            E("shexp_down", "shexp_gated"), E("shexp_scale", "shexp_gated"), E("exp_weighted", "moe_out"), E("shexp_gated", "moe_out")
        ]);
    }
}
