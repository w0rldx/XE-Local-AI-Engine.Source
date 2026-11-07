namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The GGUF descriptor joined to a PyTorch index: block N to layer N only on matching counts, and no link a rule did not find.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabPyTorchJoinerTests
{
    private const string ModelName = "org/Some-GGUF:Q4_K_M";

    /// <summary>The 27B config's index as the S0 spike wrote it, plus the contract's exportMode, anchoredLines and outsideGraphId.</summary>
    internal static ModelLabPyTorchIndex Qwen27BIndex() =>
        JsonSerializer.Deserialize<ModelLabPyTorchIndex>(File.ReadAllBytes(FixturePath), JsonSerializerOptions.Web)!;

    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "model-lab-qwen3.8-27b-pytorch-index.json");

    [Test]
    public void TheFixture_ReadsAsTheContractShape()
    {
        var index = Qwen27BIndex();

        AssertEx.Equal(expected: 64, index.Blocks.Count);
        AssertEx.Equal("representative", index.ExportMode);
        AssertEx.True(index.AnchoredLines);
        AssertEx.Equal("model.layers", index.BlockPath);
        AssertEx.False(index.Blocks.Any(block => block.GraphId == index.OutsideGraphId), "The outside graph is the one no block names.");
        AssertEx.Equal(expected: 3, index.Graphs.Count);
        AssertEx.Equal("Qwen3_5DecoderLayer", index.Blocks[0].Class);
        AssertEx.Equal("linear_attention", index.Blocks[0].LayerType!);
        AssertEx.Equal(expected: 874, index.Blocks[0].Modules[0].ForwardLine!.Value);
    }

    [Test]
    public void The27B_JoinsItsSixtyFourTrunkBlocks_AndLeavesTheMtpBlockUnlinked()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_27BLike());

        var join = ModelLabPyTorchJoiner.Join(descriptor, Qwen27BIndex());

        AssertEx.True(join.BlocksJoined, join.Reason ?? "joined");
        AssertEx.Null(join.Reason);
        AssertEx.Equal(expected: 64, join.GgufTrunkBlockCount);
        AssertEx.Equal(expected: 64, join.PyTorchBlockCount);
        var links = join.Components.ToDictionary(static link => link.ComponentId, static link => string.Join(',', link.ModulePaths), StringComparer.Ordinal);
        AssertEx.Equal("model.layers.0.linear_attn", links["blk.0.ssm"]);
        AssertEx.Equal("model.layers.0.mlp", links["blk.0.ffn"]);
        AssertEx.Equal("model.layers.0.input_layernorm,model.layers.0.post_attention_layernorm", links["blk.0.norm"]);
        AssertEx.Equal("model.layers.3.self_attn", links["blk.3.attention"]);
        AssertEx.Equal("model.layers.63.self_attn", links["blk.63.attention"]);
        AssertEx.False(links.Keys.Any(static id => id.StartsWith("blk.64.", StringComparison.Ordinal)), "The MTP block has no PyTorch layer.");
        AssertEx.Equal("model.embed_tokens", links["embedding"]);
        AssertEx.Equal("model.norm", links["output_norm"]);
        AssertEx.Equal("lm_head", links["output"]);

        // Through the in-block path: the HF prefix model.language_model.layers.N. is not the module's model.layers.N.
        var tensors = join.Tensors.ToDictionary(static link => link.GgufName, static link => link.ModulePath, StringComparer.Ordinal);
        AssertEx.Equal("model.layers.0.linear_attn", tensors["blk.0.ssm_a"]);
        AssertEx.Equal("model.layers.0.linear_attn.in_proj_qkv", tensors["blk.0.attn_qkv.weight"]);
        AssertEx.Equal("model.layers.3.self_attn.q_norm", tensors["blk.3.attn_q_norm.weight"]);
        AssertEx.Equal("model.layers.15.mlp.down_proj", tensors["blk.15.ffn_down.weight"]);
        AssertEx.False(tensors.Keys.Any(static name => name.StartsWith("blk.64.", StringComparison.Ordinal)));
    }

    [Test]
    public void ABlockCountMismatch_LinksNoBlock_SaysWhy_AndStillLinksTheOutsideModules()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B());

        var join = ModelLabPyTorchJoiner.Join(descriptor, Qwen27BIndex());

        AssertEx.False(join.BlocksJoined);
        AssertEx.Equal("blockCountMismatch", join.Reason!);
        AssertEx.Equal(expected: 24, join.GgufTrunkBlockCount);
        AssertEx.Equal(expected: 64, join.PyTorchBlockCount);
        AssertEx.Empty(join.Tensors);
        AssertEx.Equal("embedding,output_norm", string.Join(',', join.Components.Select(static link => link.ComponentId)));
    }

    [Test]
    public void AnUnreadableGguf_IsNotJoined()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName,
            Qwen35TensorFixture.Inspection("qwen35", [], GgufTensorReadFailure.Truncated));

        var join = ModelLabPyTorchJoiner.Join(descriptor, Qwen27BIndex());

        AssertEx.False(join.BlocksJoined);
        AssertEx.Equal("ggufUnreadable", join.Reason!);
        AssertEx.Empty(join.Components);
    }

    /// <summary>Another architecture has no HF names: its groups link by the block child names, and a group no rule matches gets no link.</summary>
    [Test]
    public void ALlamaGguf_LinksByChildName_AndLeavesAnUnmatchedGroupUnlinked()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("token_embd.weight", [64, 10], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up.weight", [64, 128], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.rope_freqs.weight", [32], "F32"),
            Qwen35TensorFixture.Tensor("output_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("output.weight", [64, 10], "F32")
        ]));
        var index = new ModelLabPyTorchIndex
        {
            Blocks =
            [
                new ModelLabPyTorchBlock
                {
                    Index = 0,
                    Path = "model.layers.0",
                    Modules =
                    [
                        Module("model.layers.0"),
                        Module("model.layers.0.self_attn"),
                        Module("model.layers.0.self_attn.q_proj"),
                        Module("model.layers.0.mlp"),
                        Module("model.layers.0.input_layernorm"),
                        Module("model.layers.0.post_attention_layernorm")
                    ]
                }
            ],
            Outside = [Module("model"), Module("model.embed_tokens"), Module("model.layers"), Module("model.norm"), Module("model.rotary_emb"), Module("lm_head")]
        };

        var join = ModelLabPyTorchJoiner.Join(descriptor, index);

        AssertEx.True(join.BlocksJoined);
        var links = join.Components.ToDictionary(static link => link.ComponentId, static link => string.Join(',', link.ModulePaths), StringComparer.Ordinal);
        AssertEx.Equal("model.layers.0.self_attn", links["blk.0.attention"]);
        AssertEx.Equal("model.layers.0.mlp", links["blk.0.ffn"]);
        AssertEx.Equal("model.layers.0.input_layernorm,model.layers.0.post_attention_layernorm", links["blk.0.norm"]);
        AssertEx.False(links.ContainsKey("blk.0.other"), "rope_freqs matches no rule, so it has no code link rather than a guess.");
        AssertEx.Equal("model.embed_tokens", links["embedding"]);
        AssertEx.Equal("model.norm", links["output_norm"]);
        AssertEx.Equal("lm_head", links["output"]);
        AssertEx.Empty(join.Tensors);
    }

    [Test]
    [Arguments("model.language_model.layers.3.mlp.gate_proj.weight", 3, "mlp.gate_proj")]
    [Arguments("model.language_model.layers.0.linear_attn.A_log", 0, "linear_attn")]
    [Arguments("layers.7.input_layernorm.weight", 7, "input_layernorm")]
    [Arguments("model.layers.2.scale", 2, "")]
    public void InBlockModule_StripsTheLayerPrefixAndTheParameter(string hfName, int block, string expected)
    {
        AssertEx.Equal(expected, ModelLabPyTorchJoiner.InBlockModuleOrNull(hfName, block)!);
    }

    [Test]
    [Arguments("model.language_model.layers.13.mlp.gate_proj.weight", 3)]
    [Arguments("model.sublayers.3.mlp.weight", 3)]
    [Arguments("lm_head.weight", 0)]
    public void InBlockModule_IsNull_ForAnotherLayer(string hfName, int block)
    {
        AssertEx.Null(ModelLabPyTorchJoiner.InBlockModuleOrNull(hfName, block));
    }

    private static ModelLabPyTorchModule Module(string path) =>
        new()
        {
            Path = path,
            Class = "Module"
        };
}
