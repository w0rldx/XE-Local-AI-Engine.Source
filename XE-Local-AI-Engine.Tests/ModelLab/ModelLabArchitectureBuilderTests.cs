namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
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
        AssertEx.Equal(string.Join(',', Qwen35TensorFixture.FullAttentionTemplateEdges), EdgeLines(descriptor.Templates[1]));
        AssertEx.Equal(string.Join(',', Qwen35TensorFixture.MtpTemplateEdges), EdgeLines(descriptor.Templates[2]));
    }

    [Test]
    public void AQwen36MoeFile_IsDetailed_WithThirtyLinearAndTenFullBlocks_TwoTemplates_AndNoUnmappedTensor()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35MoeTensorFixture.Qwen36_35B_A3B());

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Equal("qwen35moe", descriptor.Architecture!);
        AssertEx.Equal(Qwen35MoeTensorFixture.BlockCount, descriptor.Blocks.Count);
        AssertEx.Equal(expected: 30, descriptor.Blocks.Count(static block => block.Kind == "linearAttention"));
        AssertEx.Equal(expected: 10, descriptor.Blocks.Count(static block => block.Kind == "fullAttention"));
        var tensors = AllTensors(descriptor).ToList();
        AssertEx.Equal(expected: 733, tensors.Count);
        AssertEx.Empty(tensors.Where(static tensor => tensor.HfName is null).Select(static tensor => tensor.GgufName));
        AssertEx.Equal("linearAttention,fullAttention", string.Join(',', descriptor.Templates.Select(static template => template.Kind)));
        AssertEx.True(descriptor.Templates.All(static template => template.Source == "llama.cpp src/models/qwen35moe.cpp @ b12014"));
        AssertEx.Equal("blk.0.ssm,blk.0.ffn,blk.0.norm", string.Join(',', descriptor.Blocks[0].Groups.Select(static group => group.ComponentId)));
        // Router, three routed-expert tensors, the shared-expert gate and its three projections.
        AssertEx.Equal(expected: 8, descriptor.Blocks[0].Groups[1].Tensors.Count);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(39)]
    public void EveryRecordedMoeRow_GetsItsHfName_AndValueRelation(int blockIndex)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35MoeTensorFixture.Qwen36_35B_A3B());
        var rows = Qwen35TensorFixture.IsFull(blockIndex) ? Qwen35MoeTensorFixture.FullBlock : Qwen35MoeTensorFixture.LinearBlock;
        var byName = descriptor.Blocks[blockIndex].Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);

        AssertEx.Equal(rows.Length, byName.Count);
        foreach (var row in rows)
        {
            var tensor = byName[$"blk.{blockIndex}.{row.GgufSuffix}"];
            AssertEx.Equal($"model.language_model.layers.{blockIndex}.{row.HfSuffix}", tensor.HfName, row.GgufSuffix);
            AssertEx.Equal(row.Relation, tensor.ValueRelation, row.GgufSuffix);
        }
    }

    /// <summary>The split halves stay <c>sliced</c> whatever the stored type; the fused tensor is the HF one, so it follows the type like any equal row.</summary>
    [Test]
    [Arguments("Q4_K", "quantized")]
    [Arguments("BF16", "equal")]
    public void TheExpertHalves_StaySliced_WhileTheFusedTensorAndTheRouterFollowTheStoredType(string ggmlType, string fusedRelation)
    {
        var inspection = Qwen35TensorFixture.Inspection("qwen35moe",
        [
            Qwen35TensorFixture.Tensor("blk.0.ssm_a", [32], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate_inp.weight", [64, 8], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate_exps.weight", [64, 16, 8], ggmlType),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up_exps.weight", [64, 16, 8], ggmlType),
            Qwen35TensorFixture.Tensor("blk.0.ffn_gate_shexp.weight", [64, 16], "Q8_0"),
            Qwen35TensorFixture.Tensor("blk.1.ssm_a", [32], "F32"),
            Qwen35TensorFixture.Tensor("blk.1.ffn_gate_up_exps.weight", [64, 32, 8], ggmlType)
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("sliced", byName["blk.0.ffn_gate_exps.weight"].ValueRelation);
        AssertEx.Equal("sliced", byName["blk.0.ffn_up_exps.weight"].ValueRelation);
        AssertEx.Equal("model.language_model.layers.0.mlp.experts.gate_up_proj", byName["blk.0.ffn_up_exps.weight"].HfName);
        AssertEx.Equal("equal", byName["blk.0.ffn_gate_inp.weight"].ValueRelation);
        AssertEx.Equal("quantized", byName["blk.0.ffn_gate_shexp.weight"].ValueRelation);
        AssertEx.Equal("model.language_model.layers.1.mlp.experts.gate_up_proj", byName["blk.1.ffn_gate_up_exps.weight"].HfName);
        AssertEx.Equal(fusedRelation, byName["blk.1.ffn_gate_up_exps.weight"].ValueRelation);
    }

    /// <summary>Each family names only its own FFN: a dense row in a <c>qwen35moe</c> file and an expert row in a <c>qwen35</c> file stay unnamed.</summary>
    [Test]
    [Arguments("qwen35", "ffn_gate_exps.weight")]
    [Arguments("qwen35moe", "ffn_gate.weight")]
    public void AnotherFamilysFfnRow_GetsNoHfName(string architecture, string foreignSuffix)
    {
        var inspection = Qwen35TensorFixture.Inspection(architecture,
        [
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor($"blk.0.{foreignSuffix}", [64, 16], "F32")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.NotNull(byName["blk.0.attn_norm.weight"].HfName);
        AssertEx.Null(byName[$"blk.0.{foreignSuffix}"].HfName);
    }

    /// <summary>
    ///     Outside a linear block a fused <c>attn_qkv</c> holds <c>q_proj</c>, <c>k_proj</c> and <c>v_proj</c> together, so it gets no HF
    ///     name; a linear block's <c>attn_qkv</c> stays <c>in_proj_qkv</c>.
    /// </summary>
    [Test]
    [Arguments("qwen35")]
    [Arguments("qwen35moe")]
    public void AFusedQkvInAFullAttentionBlock_GetsNoHfName_WhileALinearBlocksQkvKeepsItsOwn(string architecture)
    {
        var inspection = Qwen35TensorFixture.Inspection(architecture,
        [
            Qwen35TensorFixture.Tensor("blk.0.ssm_a", [32], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_qkv.weight", [64, 192], "Q8_0"),
            Qwen35TensorFixture.Tensor("blk.1.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.1.attn_qkv.weight", [64, 160], "Q8_0"),
            Qwen35TensorFixture.Tensor("blk.1.attn_output.weight", [64, 64], "Q8_0")
        ], ssmGroupCount: 16, ssmTimeStepRank: 32);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("linearAttention,fullAttention", string.Join(',', descriptor.Blocks.Select(static block => block.Kind)));
        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("model.language_model.layers.0.linear_attn.in_proj_qkv.weight", byName["blk.0.attn_qkv.weight"].HfName);
        AssertEx.Equal("reordered", byName["blk.0.attn_qkv.weight"].ValueRelation);
        AssertEx.Null(byName["blk.1.attn_qkv.weight"].HfName);
        AssertEx.Null(byName["blk.1.attn_qkv.weight"].ValueRelation);
        AssertEx.Equal("model.language_model.layers.1.self_attn.o_proj.weight", byName["blk.1.attn_output.weight"].HfName);
    }

    /// <summary>
    ///     Full-attention and MTP blocks pick their Q/K/V and gate labels apart, and a linear block's delta-net <c>attn_qkv</c> never
    ///     counts: the separate side keeps today's template byte for byte, the fused side names the rows of <c>attn_qkv</c>.
    /// </summary>
    [Test]
    [Arguments("qwen35", false)]
    [Arguments("qwen35", true)]
    [Arguments("qwen35moe", false)]
    [Arguments("qwen35moe", true)]
    public void QwenFullAttentionAndMtpBlocks_PickTheirQkvLabelsApart_AndLinearBlocksNeverCount(string architecture, bool fusedTrunk)
    {
        string[] separate = ["attn_q.weight", "attn_k.weight", "attn_v.weight"];
        string[] fused = ["attn_qkv.weight"];
        var inspection = Qwen35TensorFixture.Inspection(architecture,
            new[] { "ssm_a", "attn_qkv.weight" }.Select(static suffix => $"blk.0.{suffix}")
                .Concat(new[] { "attn_norm.weight", "attn_output.weight" }.Concat(fusedTrunk ? fused : separate).Select(static suffix => $"blk.1.{suffix}"))
                .Concat(new[] { "nextn.eh_proj.weight", "attn_norm.weight", "attn_output.weight" }.Concat(fusedTrunk ? separate : fused).Select(static suffix => $"blk.2.{suffix}"))
                .Select(static name => Qwen35TensorFixture.Tensor(name, [64], "F32")));

        var templates = ModelLabArchitectureBuilder.Build(ModelName, inspection).Templates.ToDictionary(static template => template.Kind, StringComparer.Ordinal);

        var reference = SeparateQwenTemplates(architecture);
        AssertEx.Equal("linearAttention,fullAttention,mtp", string.Join(',', templates.Keys));
        AssertEx.Equal(NodeLines(reference["linearAttention"]), NodeLines(templates["linearAttention"]));
        var fusedKind = fusedTrunk ? "fullAttention" : "mtp";
        var separateKind = fusedTrunk ? "mtp" : "fullAttention";
        AssertEx.Equal(NodeLines(reference[separateKind]), NodeLines(templates[separateKind]));
        AssertEx.Equal("q: Query + gate projection · attn_q\nk: Key projection · attn_k\nv: Value projection · attn_v\ngate: sigmoid(gate half of attn_q)",
            string.Join('\n', templates[separateKind].Nodes.Where(static node => node.Id is "q" or "k" or "v" or "gate").Select(static node => $"{node.Id}: {node.Label}")));
        AssertEx.Equal(Relabelled(reference[fusedKind], FusedQwenQkv), NodeLines(templates[fusedKind]));
        foreach (var kind in templates.Keys)
        {
            AssertEx.Equal(EdgeLines(reference[kind]), EdgeLines(templates[kind]), kind);
        }
    }

    /// <summary>Full-attention blocks mixing fused and separate Q/K/V get labels true for either, while separate MTP blocks keep theirs.</summary>
    [Test]
    [Arguments("qwen35")]
    [Arguments("qwen35moe")]
    public void QwenFullAttentionBlocksMixingQkvLayouts_GetLayoutNeutralLabels_WhileSeparateMtpKeepsItsOwn(string architecture)
    {
        var inspection = Qwen35TensorFixture.Inspection(architecture,
            new[] { "blk.0.attn_norm.weight", "blk.0.attn_qkv.weight", "blk.1.attn_norm.weight", "blk.1.attn_q.weight", "blk.2.nextn.eh_proj.weight", "blk.2.attn_q.weight" }
                .Select(static name => Qwen35TensorFixture.Tensor(name, [64], "F32")));

        var templates = ModelLabArchitectureBuilder.Build(ModelName, inspection).Templates.ToDictionary(static template => template.Kind, StringComparer.Ordinal);

        var reference = SeparateQwenTemplates(architecture);
        AssertEx.Equal("fullAttention,mtp", string.Join(',', templates.Keys));
        AssertEx.Equal(Relabelled(reference["fullAttention"], MixedQwenQkv), NodeLines(templates["fullAttention"]));
        AssertEx.Equal(NodeLines(reference["mtp"]), NodeLines(templates["mtp"]));
    }

    /// <summary>
    ///     A part of a split file can list a block without its Q weight: beside separate blocks of its kind it proves no layout, so the
    ///     full-attention and MTP templates both get the layout-neutral labels.
    /// </summary>
    [Test]
    [Arguments("qwen35", "attn_q.bias")]
    [Arguments("qwen35", "attn_norm.weight")]
    [Arguments("qwen35moe", "attn_q.bias")]
    [Arguments("qwen35moe", "attn_norm.weight")]
    public void AQwenBlockWithoutAQWeight_BesideSeparateBlocks_GetsLayoutNeutralLabels(string architecture, string mtpPartTensor)
    {
        var inspection = Qwen35TensorFixture.Inspection(architecture,
            new[] { "blk.0.attn_q.weight", "blk.1.attn_q.bias", "blk.2.nextn.eh_proj.weight", "blk.2.attn_q.weight", "blk.3.nextn.eh_proj.weight", $"blk.3.{mtpPartTensor}" }
                .Select(static name => Qwen35TensorFixture.Tensor(name, [64], "F32")));

        var templates = ModelLabArchitectureBuilder.Build(ModelName, inspection).Templates.ToDictionary(static template => template.Kind, StringComparer.Ordinal);

        var reference = SeparateQwenTemplates(architecture);
        AssertEx.Equal("fullAttention,mtp", string.Join(',', templates.Keys));
        AssertEx.Equal(Relabelled(reference["fullAttention"], MixedQwenQkv), NodeLines(templates["fullAttention"]));
        AssertEx.Equal(Relabelled(reference["mtp"], MixedQwenQkv), NodeLines(templates["mtp"]));
    }

    [Test]
    public void AMoeFileWithAnMtpBlock_HasThreeTemplates_AndNoHfNameInTheMtpBlock()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35MoeTensorFixture.Qwen36_35B_A3B(withMtpBlock: true));

        AssertEx.Equal("linearAttention,fullAttention,mtp", string.Join(',', descriptor.Templates.Select(static template => template.Kind)));
        var mtp = descriptor.Blocks[Qwen35MoeTensorFixture.BlockCount];
        AssertEx.Equal("mtp", mtp.Kind);
        AssertEx.True(mtp.Groups.Any(static group => group.Component == "ffn"));
        AssertEx.True(mtp.Groups.SelectMany(static group => group.Tensors).All(static tensor => tensor.HfName is null && tensor.ValueRelation is null));
    }

    /// <summary>
    ///     Each <c>qwen35moe</c> template is the <c>qwen35</c> one of its kind with the dense FFN swapped for the frozen MoE nodes and
    ///     exactly the MoE edges, keeping the trunk's control vector and the MTP head path.
    /// </summary>
    [Test]
    [Arguments("linearAttention")]
    [Arguments("fullAttention")]
    [Arguments("mtp")]
    public void EachMoeTemplate_IsTheDenseOne_WithTheFfnSwappedForExactlyTheMoeNodesAndEdges(string kind)
    {
        string[] denseNodes = ["ffn_gate", "ffn_up", "ffn_act", "ffn_down"];
        string[] denseEdges = ["post_norm->ffn_gate", "post_norm->ffn_up", "ffn_gate->ffn_act", "ffn_up->ffn_act", "ffn_act->ffn_down", "ffn_down->ffn_residual"];
        var dense = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_27BLike()).Templates.Single(template => template.Kind == kind);
        var moe = ModelLabArchitectureBuilder.Build(ModelName, Qwen35MoeTensorFixture.Qwen36_35B_A3B(withMtpBlock: true)).Templates.Single(template => template.Kind == kind);
        var denseEdgeSet = dense.Edges.Select(static edge => $"{edge.From}->{edge.To}").ToHashSet(StringComparer.Ordinal);
        AssertEx.True(denseEdges.All(denseEdgeSet.Contains), "The dense template draws the SiLU FFN the MoE one replaces.");

        var expectedNodes = dense.Nodes.Select(static node => node.Id).Except(denseNodes, StringComparer.Ordinal).Concat(Qwen35MoeTensorFixture.MoeNodeIds);
        var expectedEdges = denseEdgeSet.Except(denseEdges, StringComparer.Ordinal).Concat(Qwen35MoeTensorFixture.MoeEdges);
        AssertEx.Equal(string.Join(',', expectedNodes.Order(StringComparer.Ordinal)), string.Join(',', moe.Nodes.Select(static node => node.Id).Order(StringComparer.Ordinal)));
        var moeEdges = moe.Edges.Select(static edge => $"{edge.From}->{edge.To}").ToList();
        AssertEx.Equal(moeEdges.Count, moeEdges.Distinct(StringComparer.Ordinal).Count());
        AssertEx.Equal(string.Join(',', expectedEdges.Order(StringComparer.Ordinal)), string.Join(',', moeEdges.Order(StringComparer.Ordinal)));
        AssertEx.True(moe.Nodes.Where(static node => Qwen35MoeTensorFixture.MoeNodeIds.Contains(node.Id, StringComparer.Ordinal)).All(static node => node.Component == "ffn"));

        string[] tail = kind == "mtp"
            ? ["ffn_residual->head_norm", "head_norm->head", "head->logits"]
            : ["ffn_residual->control_vector", "control_vector->output"];
        AssertEx.True(tail.All(moeEdges.Contains), string.Join(',', tail));
        AssertEx.Equal(kind != "mtp", moe.Nodes.Any(static node => node.Id == "control_vector"));
    }

    [Test]
    [Arguments("qwen35", 3)]
    [Arguments("qwen35moe", 3)]
    [Arguments("llama", 1)]
    [Arguments("olmoe", 1)]
    public void EveryTemplate_IsMarkedInferred_ConnectsOnlyItsOwnNodes_AndNamesOnlyComponentsItsBlocksHave(string architecture, int templateCount)
    {
        var inspection = architecture switch
        {
            "llama" => LlamaTensorFixture.Llama3_2_1B(),
            "olmoe" => OlmoeTensorFixture.Olmoe1B7B(),
            "qwen35moe" => Qwen35MoeTensorFixture.Qwen36_35B_A3B(withMtpBlock: true),
            _ => Qwen35TensorFixture.Qwen35_27BLike()
        };
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

        AssertEx.Equal("llama,olmoe,qwen35,qwen35moe", string.Join(',', descriptors.Select(static descriptor => descriptor.Architecture).Order(StringComparer.Ordinal)));
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
        AssertEx.Equal(string.Join(',', LlamaTensorFixture.TemplateEdges), EdgeLines(template));
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

    /// <summary>
    ///     A fused <c>attn_qkv</c> runs the same graph through <c>build_qkv</c>, with the Q/K/V nodes naming its rows; the table names no
    ///     source for it and nothing is guessed.
    /// </summary>
    [Test]
    public void AFusedLlamaQkv_GetsNoHfName_WhileTheRestMap_AndTheTemplateNamesTheFusedRows()
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
        AssertEx.Equal(Relabelled(SeparateLlamaTemplate(), FusedQkv), NodeLines(descriptor.Templates.Single()));
        AssertEx.Equal(string.Join(',', LlamaTensorFixture.TemplateEdges), EdgeLines(descriptor.Templates.Single()));
    }

    /// <summary>A fused <c>attn_qkv.weight</c> beside separate Q/K/V biases is still fused: the fused labels, and the biases keep their reordered HF names.</summary>
    [Test]
    public void AFusedLlamaQkvWeightWithSeparateBiases_GetsTheFusedLabels_AndTheBiasesKeepTheirHfNames()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
            Enumerable.Range(0, 2).SelectMany(static block => new[] { "attn_norm.weight", "attn_qkv.weight", "attn_q.bias", "attn_k.bias", "attn_v.bias", "attn_output.weight" }
                .Select(suffix => Qwen35TensorFixture.Tensor($"blk.{block}.{suffix}", [64], "F32"))));

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal(Relabelled(SeparateLlamaTemplate(), FusedQkv), NodeLines(descriptor.Templates.Single()));
        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("model.layers.1.self_attn.q_proj.bias reordered", $"{byName["blk.1.attn_q.bias"].HfName} {byName["blk.1.attn_q.bias"].ValueRelation}");
        AssertEx.Equal("model.layers.1.self_attn.v_proj.bias equal", $"{byName["blk.1.attn_v.bias"].HfName} {byName["blk.1.attn_v.bias"].ValueRelation}");
        AssertEx.Null(byName["blk.0.attn_qkv.weight"].HfName);
    }

    /// <summary>A Llama file mixing fused and separate Q/K/V gets labels true for either; separate-only keeps today's labels.</summary>
    [Test]
    public void ALlamaFileMixingFusedAndSeparateQkv_GetsLayoutNeutralLabels_OnTheSameGraph()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
            new[] { "attn_norm.weight", "attn_qkv.weight", "attn_output.weight" }.Select(static suffix => Qwen35TensorFixture.Tensor($"blk.0.{suffix}", [64], "F32"))
                .Concat(new[] { "attn_norm.weight", "attn_q.weight", "attn_k.weight", "attn_v.weight", "attn_output.weight" }
                    .Select(static suffix => Qwen35TensorFixture.Tensor($"blk.1.{suffix}", [64], "F32"))));

        var template = ModelLabArchitectureBuilder.Build(ModelName, inspection).Templates.Single();

        AssertEx.Equal(Relabelled(SeparateLlamaTemplate(), MixedQkv), NodeLines(template));
        AssertEx.Equal(string.Join(',', LlamaTensorFixture.TemplateEdges), EdgeLines(template));
        AssertEx.Equal("q: Query projection · attn_q\nk: Key projection · attn_k\nv: Value projection · attn_v",
            string.Join('\n', SeparateLlamaTemplate().Nodes.Where(static node => node.Id is "q" or "k" or "v").Select(static node => $"{node.Id}: {node.Label}")));
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
    public void AnOlmoeFile_IsDetailed_WithOneFullAttentionKind_OneTemplate_AndNoUnmappedTensor()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, OlmoeTensorFixture.Olmoe1B7B());

        AssertEx.Equal("detailed", descriptor.Support);
        AssertEx.Equal("olmoe", descriptor.Architecture!);
        AssertEx.Equal(OlmoeTensorFixture.BlockCount, descriptor.Blocks.Count);
        AssertEx.Equal("fullAttention", string.Join(',', descriptor.Blocks.Select(static block => block.Kind).Distinct(StringComparer.Ordinal)));
        AssertEx.Equal("blk.0.attention,blk.0.ffn,blk.0.norm", string.Join(',', descriptor.Blocks[0].Groups.Select(static group => group.ComponentId)));
        var tensors = AllTensors(descriptor).ToList();
        AssertEx.Equal(OlmoeTensorFixture.BlockCount * OlmoeTensorFixture.Block.Length + 3, tensors.Count);
        AssertEx.Empty(tensors.Where(static tensor => tensor.HfName is null).Select(static tensor => tensor.GgufName));
        var template = descriptor.Templates.Single();
        AssertEx.Equal("fullAttention", template.Kind);
        AssertEx.Equal("llama.cpp src/models/olmoe.cpp @ b12014", template.Source);
    }

    /// <summary>OLMoE's own graph: whole-projection Q/K norms before RoPE, unrenormalized top-k weights, no shared expert, the routed sum into the residual.</summary>
    [Test]
    public void TheOlmoeTemplate_HasItsOwnOrderedNodesLabelsAndEdges_AndNoSharedExpert()
    {
        var template = ModelLabArchitectureBuilder.Build(ModelName, OlmoeTensorFixture.Olmoe1B7B()).Templates.Single();

        AssertEx.Equal(string.Join('\n', OlmoeTensorFixture.TemplateNodes), string.Join('\n', template.Nodes.Select(static node => $"{node.Id}: {node.Label}")));
        AssertEx.Equal(string.Join(',', OlmoeTensorFixture.TemplateEdges), string.Join(',', template.Edges.Select(static edge => $"{edge.From}->{edge.To}")));
        AssertEx.Empty(template.Nodes.Where(static node => node.Id.StartsWith("shexp_", StringComparison.Ordinal) || node.Id == "moe_out").Select(static node => node.Id));
        string[] moeNodes = ["router", "router_probs", "topk", "topk_weights", "exp_gate", "exp_up", "exp_act", "exp_down", "exp_weighted"];
        AssertEx.True(template.Nodes.Where(node => moeNodes.Contains(node.Id, StringComparer.Ordinal)).All(static node => node.Component == "ffn"));
        AssertEx.Equal(moeNodes.Length, template.Nodes.Count(node => moeNodes.Contains(node.Id, StringComparer.Ordinal)));
    }

    [Test]
    [Arguments(0)]
    [Arguments(15)]
    public void EveryRecordedOlmoeRow_GetsItsHfName_AndValueRelation(int blockIndex)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, OlmoeTensorFixture.Olmoe1B7B());
        var byName = descriptor.Blocks[blockIndex].Groups.SelectMany(static group => group.Tensors).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);

        AssertEx.Equal(OlmoeTensorFixture.Block.Length, byName.Count);
        foreach (var row in OlmoeTensorFixture.Block)
        {
            var tensor = byName[$"blk.{blockIndex}.{row.GgufSuffix}"];
            AssertEx.Equal($"model.layers.{blockIndex}.{row.HfSuffix}", tensor.HfName, row.GgufSuffix);
            AssertEx.Equal(row.Relation, tensor.ValueRelation, row.GgufSuffix);
        }
    }

    /// <summary>
    ///     Unquantized, every OLMoE row shows its converter relation: Q and K equal (NEOX RoPE, no permutation), the Q/K norms equal (no 1 + w),
    ///     the expert gate and up halves sliced from <c>gate_up_proj</c>; a fused <c>attn_qkv</c> gets no HF name.
    /// </summary>
    [Test]
    public void AnF32OlmoeBlock_ShowsTheConverterRelationOfEveryRow_AndLeavesAFusedQkvUnnamed()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attn_norm.weight"] = "input_layernorm.weight equal",
            ["attn_q.weight"] = "self_attn.q_proj.weight equal",
            ["attn_q.bias"] = "self_attn.q_proj.bias equal",
            ["attn_k.weight"] = "self_attn.k_proj.weight equal",
            ["attn_k.bias"] = "self_attn.k_proj.bias equal",
            ["attn_v.weight"] = "self_attn.v_proj.weight equal",
            ["attn_v.bias"] = "self_attn.v_proj.bias equal",
            ["attn_output.weight"] = "self_attn.o_proj.weight equal",
            ["attn_q_norm.weight"] = "self_attn.q_norm.weight equal",
            ["attn_k_norm.weight"] = "self_attn.k_norm.weight equal",
            ["ffn_norm.weight"] = "post_attention_layernorm.weight equal",
            ["ffn_gate_inp.weight"] = "mlp.gate.weight equal",
            ["ffn_gate_exps.weight"] = "mlp.experts.gate_up_proj sliced",
            ["ffn_up_exps.weight"] = "mlp.experts.gate_up_proj sliced",
            ["ffn_down_exps.weight"] = "mlp.experts.down_proj equal"
        };
        var inspection = Qwen35TensorFixture.Inspection("olmoe",
            expected.Keys.Append("attn_qkv.weight").Select(static suffix => Qwen35TensorFixture.Tensor($"blk.3.{suffix}", [64], "F32")));

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        foreach (var (suffix, hfAndRelation) in expected)
        {
            var tensor = byName[$"blk.3.{suffix}"];
            AssertEx.Equal($"model.layers.3.{hfAndRelation}", $"{tensor.HfName} {tensor.ValueRelation}", suffix);
        }

        AssertEx.Null(byName["blk.3.attn_qkv.weight"].HfName);
        AssertEx.Null(byName["blk.3.attn_qkv.weight"].ValueRelation);
    }

    /// <summary>A file storing Q, K and V as one <c>attn_qkv</c> gets the same graph with the Q/K/V nodes naming the fused rows; that tensor stays unnamed.</summary>
    [Test]
    public void AFusedQkvOlmoeFile_GetsTheSameGraph_WithFusedRowLabels_AndAnUnnamedAttnQkv()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, OlmoeTensorFixture.Olmoe1B7B(fusedQkv: true));

        AssertEx.True(descriptor.Blocks.All(static block => block.Kind == "fullAttention"));
        var template = descriptor.Templates.Single();
        var expectedNodes = OlmoeTensorFixture.TemplateNodes.Select(static node => node switch
        {
            "q: Query projection · attn_q" => "q: Query rows of the fused projection · attn_qkv",
            "k: Key projection · attn_k" => "k: Key rows of the fused projection · attn_qkv",
            "v: Value projection · attn_v" => "v: Value rows of the fused projection · attn_qkv",
            _ => node
        });
        AssertEx.Equal(string.Join('\n', expectedNodes), string.Join('\n', template.Nodes.Select(static node => $"{node.Id}: {node.Label}")));
        AssertEx.Equal(string.Join(',', OlmoeTensorFixture.TemplateEdges), string.Join(',', template.Edges.Select(static edge => $"{edge.From}->{edge.To}")));
        var unnamed = AllTensors(descriptor).Where(static tensor => tensor.HfName is null).ToList();
        AssertEx.Equal(OlmoeTensorFixture.BlockCount, unnamed.Count);
        AssertEx.True(unnamed.All(static tensor => tensor.GgufName.EndsWith(".attn_qkv.weight", StringComparison.Ordinal) && tensor.ValueRelation is null));
    }

    /// <summary>A fused <c>attn_qkv.weight</c> beside separate Q/K/V biases is still a fused block: the biases keep their HF names, the fused weight none.</summary>
    [Test]
    public void AFusedQkvWeightWithSeparateBiases_GetsTheFusedLabels_AndTheBiasesKeepTheirHfNames()
    {
        var inspection = Qwen35TensorFixture.Inspection("olmoe",
            Enumerable.Range(0, 2).SelectMany(static block => new[] { "attn_norm.weight", "attn_qkv.weight", "attn_q.bias", "attn_k.bias", "attn_v.bias", "attn_output.weight" }
                .Select(suffix => Qwen35TensorFixture.Tensor($"blk.{block}.{suffix}", [64], "F32"))));

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal(string.Join('\n', ExpectedOlmoeNodes("Query rows of the fused projection · attn_qkv", "Key rows of the fused projection · attn_qkv",
                "Value rows of the fused projection · attn_qkv")),
            string.Join('\n', descriptor.Templates.Single().Nodes.Select(static node => $"{node.Id}: {node.Label}")));
        var byName = AllTensors(descriptor).ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("model.layers.1.self_attn.q_proj.bias equal", $"{byName["blk.1.attn_q.bias"].HfName} {byName["blk.1.attn_q.bias"].ValueRelation}");
        AssertEx.Equal("model.layers.1.self_attn.k_proj.bias equal", $"{byName["blk.1.attn_k.bias"].HfName} {byName["blk.1.attn_k.bias"].ValueRelation}");
        AssertEx.Equal("model.layers.1.self_attn.v_proj.bias equal", $"{byName["blk.1.attn_v.bias"].HfName} {byName["blk.1.attn_v.bias"].ValueRelation}");
        AssertEx.Null(byName["blk.0.attn_qkv.weight"].HfName);
        AssertEx.Null(byName["blk.1.attn_qkv.weight"].ValueRelation);
    }

    /// <summary>llama.cpp decides fused or separate per layer, so a file mixing both gets Q/K/V labels true for either, on the same graph.</summary>
    [Test]
    public void AnOlmoeFileMixingFusedAndSeparateQkv_GetsLayoutNeutralLabels_OnTheSameGraph()
    {
        var inspection = Qwen35TensorFixture.Inspection("olmoe",
            new[] { "attn_norm.weight", "attn_qkv.weight", "attn_output.weight" }.Select(static suffix => Qwen35TensorFixture.Tensor($"blk.0.{suffix}", [64], "F32"))
                .Concat(Enumerable.Range(1, 2).SelectMany(static block => new[] { "attn_norm.weight", "attn_q.weight", "attn_k.weight", "attn_v.weight", "attn_output.weight" }
                    .Select(suffix => Qwen35TensorFixture.Tensor($"blk.{block}.{suffix}", [64], "F32")))));

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.True(descriptor.Blocks.All(static block => block.Kind == "fullAttention"));
        var template = descriptor.Templates.Single();
        AssertEx.Equal(string.Join('\n', ExpectedOlmoeNodes("Query projection · attn_q or Q rows of attn_qkv", "Key projection · attn_k or K rows of attn_qkv",
                "Value projection · attn_v or V rows of attn_qkv")),
            string.Join('\n', template.Nodes.Select(static node => $"{node.Id}: {node.Label}")));
        AssertEx.Equal(string.Join(',', OlmoeTensorFixture.TemplateEdges), string.Join(',', template.Edges.Select(static edge => $"{edge.From}->{edge.To}")));
    }

    [Test]
    public void TheOlmoeNonBlockTensors_MapToTheEmbeddingTheNormAndTheUntiedHead()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, OlmoeTensorFixture.Olmoe1B7B());

        var nonBlock = descriptor.NonBlock.ToDictionary(static group => group.ComponentId, static group => group.Tensors.Single(), StringComparer.Ordinal);
        AssertEx.Equal("embedding,output,output_norm", string.Join(',', nonBlock.Keys.Order(StringComparer.Ordinal)));
        AssertEx.Equal("model.embed_tokens.weight quantized", $"{nonBlock["embedding"].HfName} {nonBlock["embedding"].ValueRelation}");
        AssertEx.Equal("model.norm.weight equal", $"{nonBlock["output_norm"].HfName} {nonBlock["output_norm"].ValueRelation}");
        AssertEx.Equal("lm_head.weight quantized", $"{nonBlock["output"].HfName} {nonBlock["output"].ValueRelation}");
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
        AssertEx.Null(descriptor.Hyperparameters);
    }

    [Test]
    public async Task Hyperparameters_OfAQwen35File_AreTheDeclaredValues_AndNullForAbsentKeys()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, await ReadAsync(new GgufHeaderBytesBuilder()
                                                                                    .WithString("general.architecture", "qwen35")
                                                                                    .WithUint32("general.file_type", value: 15)
                                                                                    .WithUint64("general.parameter_count", value: 752_393_024)
                                                                                    .WithUint32("qwen35.block_count", value: 25)
                                                                                    .WithUint32("qwen35.nextn_predict_layers", value: 1)
                                                                                    .WithUint32("qwen35.context_length", value: 262_144)
                                                                                    .WithUint32("qwen35.embedding_length", value: 1_024)
                                                                                    .WithUint32("qwen35.attention.head_count", value: 8)
                                                                                    .WithUint32("qwen35.attention.head_count_kv", value: 2)
                                                                                    .WithUint32("qwen35.attention.key_length", value: 256)
                                                                                    .WithUint32("qwen35.attention.value_length", value: 128)
                                                                                    .WithUint32("qwen35.full_attention_interval", value: 4)
                                                                                    .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                                                                                    .Build()));

        var values = AssertEx.NotNull(descriptor.Hyperparameters);
        AssertEx.Equal("Q4_K_M", values.FileType!);
        // The declared count, not the 256 elements the file's one tensor sums to.
        AssertEx.Equal(expected: 752_393_024L, values.DeclaredParamCount!.Value);
        AssertEx.Equal(expected: 25L, values.BlockCount!.Value);
        AssertEx.Equal(expected: 1L, values.NextnPredictLayers!.Value);
        AssertEx.Equal(expected: 262_144L, values.ContextLength!.Value);
        AssertEx.Equal(expected: 1_024L, values.EmbeddingLength!.Value);
        AssertEx.Equal(expected: 8L, values.AttentionHeadCount!.Value);
        AssertEx.Equal(expected: 2L, values.AttentionHeadCountKv!.Value);
        AssertEx.Equal(expected: 256L, values.AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 128L, values.AttentionValueLength!.Value);
        AssertEx.Equal(expected: 4L, values.FullAttentionInterval!.Value);
        AssertEx.Null(values.ExpertCount);
        AssertEx.Null(values.ExpertUsedCount);
        AssertEx.Null(values.SplitCount);
    }

    [Test]
    public async Task Hyperparameters_OfAnOlmoeFile_CarryTheExpertCountAndTheExpertsUsedPerToken()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, await ReadAsync(new GgufHeaderBytesBuilder()
                                                                                    .WithString("general.architecture", "olmoe")
                                                                                    .WithUint32("olmoe.block_count", value: 16)
                                                                                    .WithUint32("olmoe.expert_count", value: 64)
                                                                                    .WithUint32("olmoe.expert_used_count", value: 8)
                                                                                    .WithUint32("split.count", value: 2)
                                                                                    .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                                                                                    .Build()));

        var values = AssertEx.NotNull(descriptor.Hyperparameters);
        AssertEx.Equal(expected: 64L, values.ExpertCount!.Value);
        AssertEx.Equal(expected: 8L, values.ExpertUsedCount!.Value);
        AssertEx.Equal(expected: 16L, values.BlockCount!.Value);
        AssertEx.Equal(expected: 2L, values.SplitCount!.Value);
        AssertEx.Null(values.FullAttentionInterval);
        AssertEx.Null(values.DeclaredParamCount);
        AssertEx.Null(values.FileType);
    }

    /// <summary>A JavaScript client reads numbers as doubles, so only 0..2^53-1 reaches the wire; zero stays zero.</summary>
    [Test]
    [Arguments(0UL, 0L)]
    [Arguments(9_007_199_254_740_991UL, 9_007_199_254_740_991L)]
    [Arguments(9_007_199_254_740_992UL, null)]
    [Arguments(ulong.MaxValue, null)]
    public async Task Hyperparameters_PassOnlyJavaScriptSafeIntegers(ulong declared, long? expected)
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, await ReadAsync(new GgufHeaderBytesBuilder()
                                                                                    .WithString("general.architecture", "qwen35")
                                                                                    .WithUint64("general.parameter_count", declared)
                                                                                    .WithUint64("qwen35.expert_count", declared)
                                                                                    .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                                                                                    .Build()));

        var values = AssertEx.NotNull(descriptor.Hyperparameters);
        AssertEx.Equal(expected, values.DeclaredParamCount);
        AssertEx.Equal(expected, values.ExpertCount);
    }

    [Test]
    public async Task Hyperparameters_DropANegativeValue()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, await ReadAsync(new GgufHeaderBytesBuilder()
                                                                                    .WithString("general.architecture", "qwen35")
                                                                                    .WithRawValue("qwen35.context_length", type: 5, BitConverter.GetBytes(-1))
                                                                                    .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                                                                                    .Build()));

        AssertEx.Null(AssertEx.NotNull(descriptor.Hyperparameters).ContextLength);
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

    private static async Task<GgufTensorInspection> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await GgufTensorBlockReader.ReadAsync(stream, CancellationToken.None);
    }

    // The separate-tensor OLMoE nodes as "id: label", with the q, k and v labels replaced.
    private static IEnumerable<string> ExpectedOlmoeNodes(string queryLabel, string keyLabel, string valueLabel) =>
        OlmoeTensorFixture.TemplateNodes.Select(node => node switch
        {
            "q: Query projection · attn_q" => $"q: {queryLabel}",
            "k: Key projection · attn_k" => $"k: {keyLabel}",
            "v: Value projection · attn_v" => $"v: {valueLabel}",
            _ => node
        });

    private static readonly Dictionary<string, string> FusedQkv = new(StringComparer.Ordinal)
    {
        ["q"] = "Query rows of the fused projection · attn_qkv",
        ["k"] = "Key rows of the fused projection · attn_qkv",
        ["v"] = "Value rows of the fused projection · attn_qkv"
    };

    private static readonly Dictionary<string, string> MixedQkv = new(StringComparer.Ordinal)
    {
        ["q"] = "Query projection · attn_q or Q rows of attn_qkv",
        ["k"] = "Key projection · attn_k or K rows of attn_qkv",
        ["v"] = "Value projection · attn_v or V rows of attn_qkv"
    };

    private static readonly Dictionary<string, string> FusedQwenQkv = new(FusedQkv, StringComparer.Ordinal)
    {
        ["q"] = "Query + gate rows of the fused projection · attn_qkv",
        ["gate"] = "sigmoid(gate half of the Q rows of attn_qkv)"
    };

    private static readonly Dictionary<string, string> MixedQwenQkv = new(MixedQkv, StringComparer.Ordinal)
    {
        ["q"] = "Query + gate projection · attn_q or Q + gate rows of attn_qkv",
        ["gate"] = "sigmoid(gate half of attn_q or of the Q rows of attn_qkv)"
    };

    private static ModelLabArchitectureTemplate SeparateLlamaTemplate() =>
        ModelLabArchitectureBuilder.Build(ModelName, LlamaTensorFixture.Llama3_2_1B()).Templates.Single();

    // The templates of a separate-tensor file of the family, one per kind.
    private static Dictionary<string, ModelLabArchitectureTemplate> SeparateQwenTemplates(string architecture) =>
        ModelLabArchitectureBuilder.Build(ModelName, architecture == "qwen35moe" ? Qwen35MoeTensorFixture.Qwen36_35B_A3B(withMtpBlock: true) : Qwen35TensorFixture.Qwen35_27BLike())
                                   .Templates.ToDictionary(static template => template.Kind, StringComparer.Ordinal);

    // A template's nodes as "id: label" lines, with the given nodes relabelled.
    private static string Relabelled(ModelLabArchitectureTemplate template, IReadOnlyDictionary<string, string> labels) =>
        string.Join('\n', template.Nodes.Select(node => $"{node.Id}: {labels.GetValueOrDefault(node.Id, node.Label)}"));

    private static string NodeLines(ModelLabArchitectureTemplate template) =>
        string.Join('\n', template.Nodes.Select(static node => $"{node.Id}: {node.Label}"));

    private static string EdgeLines(ModelLabArchitectureTemplate template) =>
        string.Join(',', template.Edges.Select(static edge => $"{edge.From}->{edge.To}"));

    private static IEnumerable<ModelLabArchitectureTensor> AllTensors(ModelLabArchitectureResponse descriptor) =>
        descriptor.Blocks.SelectMany(static block => block.Groups).Concat(descriptor.NonBlock).SelectMany(static group => group.Tensors);
}
