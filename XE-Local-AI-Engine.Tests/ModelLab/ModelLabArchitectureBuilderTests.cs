namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The architecture descriptor built from a header inspection: detailed for a registered architecture, an overview otherwise, nothing invented.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabArchitectureBuilderTests
{
    private const string ModelName = "org/Some-GGUF:Q4_K_M";

    [Test]
    public void The0_8B_IsDetailed_WithEighteenLinearAndSixFullBlocks_AndNoUnmappedTensor()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B());

        AssertEx.Equal(expected: 1, descriptor.DescriptorVersion);
        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Null(descriptor.ReadFailure);
        AssertEx.Equal(expected: 24, descriptor.Blocks.Count);
        foreach (var block in descriptor.Blocks)
        {
            AssertEx.Equal(Qwen35TensorFixture.IsFull(block.Index) ? "fullAttention" : "linearAttention", block.Kind);
        }

        AssertEx.Equal(expected: 18, descriptor.Blocks.Count(static block => block.Kind == "linearAttention"));
        var tensors = AllTensors(descriptor).ToList();
        AssertEx.Equal(expected: 320, tensors.Count);
        AssertEx.Empty(tensors.Where(static tensor => tensor.HfName is null).Select(static tensor => tensor.GgufName));
    }

    [Test]
    [Arguments(14)]
    [Arguments(15)]
    public void EveryRecordedRow_GetsItsHfName_AndValueRelation(int blockIndex)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B());
        var rows = Qwen35TensorFixture.IsFull(blockIndex) ? Qwen35TensorFixture.FullBlock : Qwen35TensorFixture.LinearBlock;
        var block = descriptor.Blocks.Single(block => block.Index == blockIndex);
        var byName = block.Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);

        AssertEx.Equal(rows.Length, byName.Count);
        foreach (var row in rows)
        {
            var tensor = byName[$"blk.{blockIndex}.{row.GgufSuffix}"];
            AssertEx.Equal($"model.language_model.layers.{blockIndex}.{row.HfSuffix}", tensor.HfName!);
            AssertEx.Equal(row.Relation, tensor.ValueRelation!);
            AssertEx.Equal(row.GgmlType, tensor.GgmlType!);
            AssertEx.Equal(string.Join(',', row.Shape), string.Join(',', tensor.Shape));
        }
    }

    [Test]
    public void FewerKeyThanValueHeads_MarksExactlyThePermutedLinearTensorsReordered_EvenWhenQuantized()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_27BLike());

        foreach (var index in new[]
                 {
                     0,
                     3
                 })
        {
            var rows = Qwen35TensorFixture.IsFull(index) ? Qwen35TensorFixture.FullBlock : Qwen35TensorFixture.LinearBlock;
            var byName = descriptor.Blocks[index].Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var expected = Qwen35TensorFixture.ValueHeadReordered.Contains(row.GgufSuffix, StringComparer.Ordinal) ? "reordered" : row.Relation;
                AssertEx.Equal(expected, byName[$"blk.{index}.{row.GgufSuffix}"].ValueRelation, row.GgufSuffix);
            }
        }

        var reordered = AllTensors(descriptor).Count(static tensor => tensor.ValueRelation == "reordered");
        AssertEx.Equal(48 * Qwen35TensorFixture.ValueHeadReordered.Length, reordered);
    }

    [Test]
    public void UnstatedHeadCounts_LeaveThePermutableTensorsRelationNull_AndTheRestAsRecorded()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B(keyHeads: 16, valueHeads: null));

        var byName = descriptor.Blocks[14].Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        foreach (var row in Qwen35TensorFixture.LinearBlock)
        {
            var tensor = byName[$"blk.14.{row.GgufSuffix}"];
            AssertEx.NotNull(tensor.HfName);
            if (Qwen35TensorFixture.ValueHeadReordered.Contains(row.GgufSuffix, StringComparer.Ordinal))
            {
                AssertEx.Null(tensor.ValueRelation, row.GgufSuffix);
            }
            else
            {
                AssertEx.Equal(row.Relation, tensor.ValueRelation, row.GgufSuffix);
            }
        }
    }

    [Test]
    public void ALinearBlock_GroupsItsQkvAndGateWithTheDeltaNet_AndAFullBlockWithAttention()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B());

        var linear = descriptor.Blocks[14];
        AssertEx.Equal("blk.14.ssm,blk.14.ffn,blk.14.norm", string.Join(',', linear.Groups.Select(static group => group.ComponentId)));
        AssertEx.Equal(expected: 9, linear.Groups[0].Tensors.Count);
        AssertEx.True(linear.Groups[0].Tensors.Any(static tensor => tensor.GgufName == "blk.14.attn_qkv.weight"));

        var full = descriptor.Blocks[15];
        AssertEx.Equal("attention,ffn,norm", string.Join(',', full.Groups.Select(static group => group.Component)));
        AssertEx.Equal(expected: 6, full.Groups[0].Tensors.Count);
        AssertEx.True(full.Groups.All(static group => group.Provenance == "read"));
        AssertEx.Equal(Qwen35TensorFixture.FullBlock.Sum(static row => row.Shape.Aggregate(1L, static (product, dimension) => product * dimension)), full.ByteSize!.Value);
    }

    [Test]
    public void TheTiedOutput_HasNoOutputGroup_AndTheOtherNonBlockTensorsMapWithTheirRelation()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B());

        AssertEx.Equal("embedding,output_norm", string.Join(',', descriptor.NonBlock.Select(static group => group.ComponentId)));
        var embedding = descriptor.NonBlock[0].Tensors.Single();
        AssertEx.Equal("model.language_model.embed_tokens.weight", embedding.HfName!);
        AssertEx.Equal("quantized", embedding.ValueRelation!);
        var norm = descriptor.NonBlock[1].Tensors.Single();
        AssertEx.Equal("model.language_model.norm.weight", norm.HfName!);
        AssertEx.Equal("plus_one", norm.ValueRelation!);
        AssertEx.Equal("norm", descriptor.NonBlock[1].Component);
    }

    [Test]
    public void The27BLayout_HasAnMtpBlock64_WithNoHfNames_AndAnUntiedHead()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_27BLike());

        AssertEx.Equal(expected: 65, descriptor.Blocks.Count);
        AssertEx.Equal(expected: 48, descriptor.Blocks.Count(static block => block.Kind == "linearAttention"));
        AssertEx.Equal(expected: 16, descriptor.Blocks.Count(static block => block.Kind == "fullAttention"));
        var mtp = descriptor.Blocks[64];
        AssertEx.Equal("mtp", mtp.Kind);
        var mtpGroup = mtp.Groups.Single(static group => group.ComponentId == "blk.64.mtp");
        AssertEx.Equal(Qwen35TensorFixture.NextnTensors.Length, mtpGroup.Tensors.Count);
        AssertEx.True(mtp.Groups.SelectMany(static group => group.Tensors).All(static tensor => tensor.HfName is null && tensor.ValueRelation is null));
        AssertEx.Equal("model.language_model.layers.63.mlp.down_proj.weight",
            descriptor.Blocks[63].Groups.Single(static group => group.Component == "ffn").Tensors.Single(static tensor => tensor.GgufName == "blk.63.ffn_down.weight").HfName!);
        AssertEx.Equal("lm_head.weight", descriptor.NonBlock.Single(static group => group.ComponentId == "output").Tensors.Single().HfName!);
        AssertEx.Equal("linearAttention,fullAttention,mtp", string.Join(',', descriptor.Templates.Select(static template => template.Kind)));
    }

    [Test]
    [Arguments("qwen35", 3)]
    [Arguments("llama", 1)]
    public void EveryTemplate_IsMarkedInferred_ConnectsOnlyItsOwnNodes_AndNamesOnlyComponentsItsBlocksHave(string architecture, int templateCount)
    {
        var inspection = architecture == "llama" ? LlamaTensorFixture.Llama3_2_1B() : Qwen35TensorFixture.Qwen35_27BLike();
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal(templateCount, descriptor.Templates.Count);
        foreach (var template in descriptor.Templates)
        {
            AssertEx.Equal("inferred", template.Provenance);
            AssertEx.Equal($"llama.cpp src/models/{architecture}.cpp @ b12014", template.Source);
            var ids = template.Nodes.Select(static node => node.Id).ToList();
            AssertEx.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
            AssertEx.Empty(template.Edges.Where(edge => !ids.Contains(edge.From) || !ids.Contains(edge.To)).Select(static edge => $"{edge.From}->{edge.To}"));
            // One node has no outgoing edge: the block output, or the draft logits for MTP.
            AssertEx.Equal(expected: 1, template.Nodes.Count(node => template.Edges.All(edge => edge.From != node.Id)));

            var components = descriptor.Blocks.Where(block => block.Kind == template.Kind)
                                       .SelectMany(static block => block.Groups.Select(static group => group.Component))
                                       .ToHashSet(StringComparer.Ordinal);
            AssertEx.Empty(template.Nodes.Where(node => node.Component is { } component && !components.Contains(component)).Select(static node => node.Id));
        }
    }

    [Test]
    public void TheTemplates_WereCheckedAgainstThePinnedLlamaCppTag()
    {
        var descriptors = ModelLabArchitectureBuilder.RegisteredDescriptors.ToList();

        AssertEx.NotEmpty(descriptors);
        // A pin bump fails here on purpose: re-read each descriptor's upstream files at the new tag first.
        foreach (var descriptor in descriptors)
        {
            AssertEx.Equal(LlamaCppReleasePins.PinnedTag, descriptor.LlamaCppTag, descriptor.Architecture);
        }
    }

    [Test]
    public void AnotherArchitecture_IsAnOverview_WithNameGroupsAndNothingInvented()
    {
        var inspection = Qwen35TensorFixture.Inspection("xe-unregistered-arch",
        [
            Qwen35TensorFixture.Tensor("token_embd.weight", [64, 100], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_k.weight", [64, 16], "Q4_K") with
            {
                ByteSize = null
            },
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up.weight", [64, 128], "Q4_K"),
            Qwen35TensorFixture.Tensor("rope_freqs.weight", [32], "F32")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("overview", descriptor.Support);
        AssertEx.Equal("xe-unregistered-arch", descriptor.Architecture!);
        AssertEx.Empty(descriptor.Templates);
        var block = descriptor.Blocks.Single();
        AssertEx.Equal("fullAttention", block.Kind);
        AssertEx.Null(block.ByteSize);
        AssertEx.Equal("blk.0.attention,blk.0.ffn,blk.0.norm", string.Join(',', block.Groups.Select(static group => group.ComponentId)));
        AssertEx.Equal("embedding,other", string.Join(',', descriptor.NonBlock.Select(static group => group.ComponentId)));
        AssertEx.True(AllTensors(descriptor).All(static tensor => tensor.HfName is null && tensor.ValueRelation is null));
    }

    [Test]
    public void ALlama3_2_1B_IsDetailed_WithOneFullAttentionTemplate_AndOnlyRopeFreqsUnmapped()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, LlamaTensorFixture.Llama3_2_1B());

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Equal(LlamaTensorFixture.BlockCount, descriptor.Blocks.Count);
        AssertEx.True(descriptor.Blocks.All(static block => block.Kind == "fullAttention"));
        var template = descriptor.Templates.Single();
        AssertEx.Equal("fullAttention", template.Kind);
        AssertEx.Equal(string.Join(',', LlamaTensorFixture.TemplateNodeIds), string.Join(',', template.Nodes.Select(static node => node.Id)));
        AssertEx.Equal("rope_freqs.weight", string.Join(',', AllTensors(descriptor).Where(static tensor => tensor.HfName is null).Select(static tensor => tensor.GgufName)));
        var nonBlock = descriptor.NonBlock.ToDictionary(static group => group.ComponentId, static group => group.Tensors.Single(), StringComparer.Ordinal);
        AssertEx.Equal("embedding,other,output_norm", string.Join(',', nonBlock.Keys.Order(StringComparer.Ordinal)));
        AssertEx.Equal("model.embed_tokens.weight", nonBlock["embedding"].HfName!);
        AssertEx.Equal("model.norm.weight", nonBlock["output_norm"].HfName!);
        AssertEx.Equal("equal", nonBlock["output_norm"].ValueRelation!);
    }

    [Test]
    [Arguments(0)]
    [Arguments(15)]
    public void EveryRecordedLlamaRow_GetsItsHfName_AndValueRelation(int blockIndex)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, LlamaTensorFixture.Llama3_2_1B());
        var byName = descriptor.Blocks[blockIndex].Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);

        AssertEx.Equal(LlamaTensorFixture.Block.Length, byName.Count);
        foreach (var row in LlamaTensorFixture.Block)
        {
            var tensor = byName[$"blk.{blockIndex}.{row.GgufSuffix}"];
            AssertEx.Equal($"model.layers.{blockIndex}.{row.HfSuffix}", tensor.HfName!);
            AssertEx.Equal(row.Relation, tensor.ValueRelation!, row.GgufSuffix);
        }
    }

    /// <summary>The converter permutes Q and K weights and biases for NORM RoPE; V, the norms and the FFN keep their values.</summary>
    [Test]
    public void AnF32LlamaBlock_KeepsQAndKReordered_WithTheirBiases_AndTheRestEqual()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.bias", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_k.weight", [64, 16], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_k.bias", [16], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_v.weight", [64, 16], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_v.bias", [16], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_output.weight", [64, 64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate.weight", [64, 128], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up.weight", [64, 128], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_down.weight", [128, 64], "F32")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        var relations = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, static tensor => tensor.ValueRelation, StringComparer.Ordinal);
        string[] reordered = ["blk.0.attn_q.weight", "blk.0.attn_q.bias", "blk.0.attn_k.weight", "blk.0.attn_k.bias"];
        foreach (var (name, relation) in relations)
        {
            AssertEx.Equal(reordered.Contains(name, StringComparer.Ordinal) ? "reordered" : "equal", relation, name);
        }

        AssertEx.Equal("model.layers.0.self_attn.k_proj.bias", AllTensors(descriptor).Single(static tensor => tensor.GgufName == "blk.0.attn_k.bias").HfName!);
    }

    /// <summary>A fused <c>attn_qkv</c> runs the same graph through <c>build_qkv</c>, but the table names no source for it and nothing is guessed.</summary>
    [Test]
    public void AFusedLlamaQkv_GetsNoHfName_WhileTheRestMap_AndTheTemplateStays()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_qkv.weight", [64, 96], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_output.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up.weight", [64, 128], "Q4_K")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Equal("fullAttention", descriptor.Blocks.Single().Kind);
        var qkv = AllTensors(descriptor).Single(static tensor => tensor.GgufName == "blk.0.attn_qkv.weight");
        AssertEx.Null(qkv.HfName);
        AssertEx.Null(qkv.ValueRelation);
        AssertEx.True(AllTensors(descriptor).Where(tensor => tensor != qkv).All(static tensor => tensor.HfName is not null));
        AssertEx.Equal("fullAttention", descriptor.Templates.Single().Kind);
    }

    [Test]
    public void AMixtralStyleLlama_NamesItsAttentionAndRouter_ButGetsNoTemplate()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate_inp.weight", [64, 8], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate_exps.weight", [64, 128, 8], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up_exps.weight", [64, 128, 8], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_down_exps.weight", [128, 64, 8], "Q4_K")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Empty(descriptor.Templates);
        var names = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, static tensor => tensor.HfName, StringComparer.Ordinal);
        AssertEx.Equal("model.layers.0.self_attn.q_proj.weight", names["blk.0.attn_q.weight"]);
        AssertEx.Equal("model.layers.0.block_sparse_moe.gate.weight", names["blk.0.ffn_gate_inp.weight"]);
        AssertEx.Null(names["blk.0.ffn_up_exps.weight"]);
    }

    /// <summary>The router alone and an expert tensor alone each decline the dense template, without any expert-count metadata.</summary>
    [Test]
    [Arguments("ffn_gate_inp.weight")]
    [Arguments("ffn_up_exps.weight")]
    public void ALlamaBlock_WithOneMoeTensorAlone_GetsNoTemplate(string moeSuffix)
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor($"blk.0.{moeSuffix}", [64, 128, 8], "Q4_K")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Equal("fullAttention", descriptor.Blocks.Single().Kind);
        AssertEx.Empty(descriptor.Templates);
    }

    /// <summary>An untied checkpoint stores its own output head: it maps to <c>lm_head.weight</c>, equal when float, quantized otherwise.</summary>
    [Test]
    [Arguments("F32", "equal")]
    [Arguments("Q6_K", "quantized")]
    public void AnUntiedLlama_MapsOutputWeightToLmHead(string ggmlType, string relation)
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("token_embd.weight", [64, 256], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("output_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("output.weight", [64, 256], ggmlType)
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        var output = AllTensors(descriptor).Single(static tensor => tensor.GgufName == "output.weight");
        AssertEx.Equal("lm_head.weight", output.HfName!);
        AssertEx.Equal(relation, output.ValueRelation!);
    }

    /// <summary>A later shard lists no router tensor, and only the first shard carries metadata: either signal alone declines the dense template.</summary>
    [Test]
    public void ASplitLlamaShard_GetsNoTemplate_WhenTheMetadataSaysExperts_OrItListsNoBlock()
    {
        var attentionOnly = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_k.weight", [64, 16], "Q4_K")
        ], expertCount: 8, splitCount: 2);
        var tensorlessFirstShard = Qwen35TensorFixture.Inspection("llama", [], expertCount: 0, splitCount: 2);

        var moeShard = ModelLabArchitectureBuilder.Build(ModelName, attentionOnly);
        var firstShard = ModelLabArchitectureBuilder.Build(ModelName, tensorlessFirstShard);

        AssertEx.Equal("fullAttention", moeShard.Blocks.Single().Kind);
        AssertEx.Empty(moeShard.Templates);
        AssertEx.Equal("model.layers.0.self_attn.k_proj.weight", AllTensors(moeShard).Single(static tensor => tensor.GgufName == "blk.0.attn_k.weight").HfName!);
        AssertEx.Equal("detailed", firstShard.Support);
        AssertEx.Empty(firstShard.Blocks);
        AssertEx.Empty(firstShard.Templates);
    }

    [Test]
    public void AnUnreadableFile_SaysWhy_AndListsNothing()
    {
        var inspection = Qwen35TensorFixture.Inspection("qwen35", [Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32")],
            GgufTensorReadFailure.UnsupportedVersion);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("unreadable", descriptor.Support);
        AssertEx.Equal("unsupportedVersion", descriptor.ReadFailure!);
        AssertEx.Empty(descriptor.Blocks);
        AssertEx.Empty(descriptor.Templates);
        AssertEx.Empty(descriptor.NonBlock);
    }

    [Test]
    [Arguments("https://huggingface.co/Qwen/Qwen3.5-0.8B", null, "Qwen/Qwen3.5-0.8B")]
    [Arguments("https://huggingface.co/Qwen/Qwen3.5-0.8B/", null, "Qwen/Qwen3.5-0.8B")]
    [Arguments("https://HuggingFace.co/Qwen/Qwen3.5-0.8B", null, "Qwen/Qwen3.5-0.8B")]
    [Arguments(null, "https://huggingface.co/Qwen/Qwen3.8-27B", "Qwen/Qwen3.8-27B")]
    [Arguments("https://huggingface.co/unsloth/Qwen3.5-0.8B", "https://huggingface.co/Qwen/Qwen3.5-0.8B", "unsloth/Qwen3.5-0.8B")]
    [Arguments("not a url", "https://huggingface.co/Qwen/Qwen3.5-0.8B", "Qwen/Qwen3.5-0.8B")]
    public void SuggestedBaseRepo_IsTheSourceRepo_ElseTheFirstBaseModel(string? source, string? baseModel, string expected)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Inspection("qwen35", [], sourceRepoUrl: source, baseModelRepoUrl: baseModel));

        AssertEx.Equal(expected, descriptor.SuggestedBaseRepo!);
    }

    /// <summary>Only <c>https://huggingface.co/owner/name</c> is a prefill: a mirror, a path below the repo or a dataset names no model repo to fetch.</summary>
    [Test]
    [Arguments("http://huggingface.co/Qwen/Qwen3.5-0.8B")]
    [Arguments("https://hf-mirror.com/Qwen/Qwen3.5-0.8B")]
    [Arguments("https://huggingface.co.evil.example/Qwen/Qwen3.5-0.8B")]
    [Arguments("https://huggingface.co/Qwen/Qwen3.5-0.8B/tree/main")]
    [Arguments("https://huggingface.co/Qwen")]
    [Arguments("https://huggingface.co/Qwen/Qwen3.5-0.8B?x=1")]
    [Arguments("https://huggingface.co:8443/Qwen/Qwen3.5-0.8B")]
    [Arguments("https://user@huggingface.co/Qwen/Qwen3.5-0.8B")]
    [Arguments("https://huggingface.co/datasets/owner")]
    [Arguments("https://huggingface.co/Qwen/..")]
    [Arguments("Qwen/Qwen3.5-0.8B")]
    [Arguments("")]
    public void SuggestedBaseRepo_IsNull_ForAnyOtherUrl(string url)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Inspection("qwen35", [], sourceRepoUrl: url, baseModelRepoUrl: url));

        AssertEx.Null(descriptor.SuggestedBaseRepo);
    }

    [Test]
    public void SuggestedBaseRepo_IsNull_WhenTheHeaderNamesNoRepo()
    {
        AssertEx.Null(ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_0_8B()).SuggestedBaseRepo);
    }

    private static IEnumerable<ModelLabArchitectureTensor> AllTensors(ModelLabArchitectureResponse descriptor) =>
        descriptor.Blocks.SelectMany(static block => block.Groups).Concat(descriptor.NonBlock).SelectMany(static group => group.Tensors);
}
