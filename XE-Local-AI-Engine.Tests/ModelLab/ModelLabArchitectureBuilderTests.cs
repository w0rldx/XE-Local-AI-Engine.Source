namespace XE_Local_AI_Engine.Tests.ModelLab;

using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The architecture descriptor built from a header inspection: detailed for <c>qwen35</c>, an overview otherwise, nothing invented.</summary>
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

        foreach (var index in new[] { 0, 3 })
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
    public void EveryTemplate_IsMarkedInferred_ConnectsOnlyItsOwnNodes_AndNamesOnlyComponentsItsBlocksHave()
    {
        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Qwen35_27BLike());

        AssertEx.Equal(expected: 3, descriptor.Templates.Count);
        foreach (var template in descriptor.Templates)
        {
            AssertEx.Equal("inferred", template.Provenance);
            AssertEx.Equal("llama.cpp src/models/qwen35.cpp @ b12014", template.Source);
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
        // A pin bump fails here on purpose: re-read src/models/qwen35.cpp and conversion/qwen.py at the new tag first.
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, ModelLabQwen35Architecture.TemplateLlamaCppTag);
    }

    [Test]
    public void AnotherArchitecture_IsAnOverview_WithNameGroupsAndNothingInvented()
    {
        var inspection = Qwen35TensorFixture.Inspection("llama",
        [
            Qwen35TensorFixture.Tensor("token_embd.weight", [64, 100], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.attn_q.weight", [64, 64], "Q4_K"),
            Qwen35TensorFixture.Tensor("blk.0.attn_k.weight", [64, 16], "Q4_K") with { ByteSize = null },
            Qwen35TensorFixture.Tensor("blk.0.ffn_norm.weight", [64], "F32"),
            Qwen35TensorFixture.Tensor("blk.0.ffn_up.weight", [64, 128], "Q4_K"),
            Qwen35TensorFixture.Tensor("rope_freqs.weight", [32], "F32")
        ]);

        var descriptor = ModelLabArchitectureBuilder.Build(ModelName, inspection);

        AssertEx.Equal("overview", descriptor.Support);
        AssertEx.Equal("llama", descriptor.Architecture!);
        AssertEx.Empty(descriptor.Templates);
        var block = descriptor.Blocks.Single();
        AssertEx.Equal("fullAttention", block.Kind);
        AssertEx.Null(block.ByteSize);
        AssertEx.Equal("blk.0.attention,blk.0.ffn,blk.0.norm", string.Join(',', block.Groups.Select(static group => group.ComponentId)));
        AssertEx.Equal("embedding,other", string.Join(',', descriptor.NonBlock.Select(static group => group.ComponentId)));
        AssertEx.True(AllTensors(descriptor).All(static tensor => tensor.HfName is null && tensor.ValueRelation is null));
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

    private static IEnumerable<ModelLabArchitectureTensor> AllTensors(ModelLabArchitectureResponse descriptor) =>
        descriptor.Blocks.SelectMany(static block => block.Groups).Concat(descriptor.NonBlock).SelectMany(static group => group.Tensors);
}
