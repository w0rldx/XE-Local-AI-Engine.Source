namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GET <c>model-lab/architecture/{modelName}</c>: operator only, the file comes from the model registry, an unregistered
///     name is a 404, and a registered GGUF is read by the real metadata reader into the descriptor.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabArchitectureEndpointTests
{
    private const string RegisteredModel = "org/Tiny-GGUF:Q4_K_M";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Anonymous_Is401()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Uri.EscapeDataString(RegisteredModel)));
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ANonOperator_Is403()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Uri.EscapeDataString(RegisteredModel)));
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task AnUnregisteredModel_Is404()
    {
        using var dir = new TempDirectory("xe-hf-test");
        await using var factory = CreateFactory(dir.FilePath("tiny.gguf"));
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "org%2FUnknown-GGUF%3AQ4_K_M");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ARegisteredQwen35Gguf_IsDescribedInDetail()
    {
        using var dir = new TempDirectory("xe-hf-test");
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path,
            new GgufHeaderBytesBuilder()
                .WithString("general.architecture", "qwen35")
                .WithUint32("qwen35.block_count", value: 1)
                .WithUint32("qwen35.ssm.group_count", value: 16)
                .WithUint32("qwen35.ssm.time_step_rank", value: 48)
                .WithTensor("token_embd.weight", [256, 10], ggmlType: 12, offset: 0)
                .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 1_440)
                .WithTensor("blk.0.ssm_a", [16], ggmlType: 0, offset: 2_464)
                .WithTensor("blk.0.attn_qkv.weight", [256, 4], ggmlType: 12, offset: 2_528)
                .WithTensor("output_norm.weight", [256], ggmlType: 0, offset: 3_104)
                .Build());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabArchitectureResponse>(JsonOptions));
        AssertEx.Equal(RegisteredModel, body.ModelName);
        AssertEx.Equal("detailed", body.Support);
        AssertEx.Equal("qwen35", body.Architecture);
        var block = body.Blocks.Single();
        AssertEx.Equal("linearAttention", block.Kind);
        // 1 024 bytes of F32 norm, 64 of F32 ssm_a and four 144-byte Q4_K rows.
        AssertEx.Equal(expected: 1_024L + 64 + 576, block.ByteSize!.Value);
        AssertEx.Equal("blk.0.ssm,blk.0.norm", string.Join(',', block.Groups.Select(static group => group.ComponentId)));
        var ssmA = block.Groups[0].Tensors.Single(static tensor => tensor.GgufName == "blk.0.ssm_a");
        AssertEx.Equal("model.language_model.layers.0.linear_attn.A_log", ssmA.HfName);
        // 16 key heads against 48 value heads: the converter permuted ssm_a's value heads.
        AssertEx.Equal("reordered", ssmA.ValueRelation);
        AssertEx.Equal("plus_one", block.Groups[1].Tensors.Single().ValueRelation);
        AssertEx.Equal("F32", ssmA.GgmlType);
        AssertEx.Equal("linearAttention", body.Templates.Single().Kind);
        AssertEx.Equal("embedding,output_norm", string.Join(',', body.NonBlock.Select(static group => group.ComponentId)));

        // The wire names the frontend reads, independent of the C# property names.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tensor = json.RootElement.GetProperty("blocks")[0].GetProperty("groups")[0].GetProperty("tensors")[0];
        AssertEx.Equal("blk.0.ssm_a", tensor.GetProperty("ggufName").GetString());
        AssertEx.Equal(JsonValueKind.Array, tensor.GetProperty("shape").ValueKind);
        AssertEx.Equal(expected: 1, json.RootElement.GetProperty("descriptorVersion").GetInt32());
    }

    [Test]
    public async Task ARegisteredQwen35MoeGguf_IsDescribedInDetail_WithSlicedExpertHalvesAndTheMoeTemplate()
    {
        using var dir = new TempDirectory("xe-hf-test");
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path,
            new GgufHeaderBytesBuilder()
                .WithString("general.architecture", "qwen35moe")
                .WithUint32("qwen35moe.block_count", value: 1)
                .WithUint32("qwen35moe.expert_count", value: 8)
                .WithUint32("qwen35moe.ssm.group_count", value: 16)
                .WithUint32("qwen35moe.ssm.time_step_rank", value: 32)
                .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                .WithTensor("blk.0.ssm_a", [16], ggmlType: 0, offset: 1_024)
                .WithTensor("blk.0.ffn_gate_inp.weight", [256, 8], ggmlType: 0, offset: 1_088)
                .WithTensor("blk.0.ffn_gate_exps.weight", [256, 4, 8], ggmlType: 12, offset: 9_280)
                .WithTensor("blk.0.ffn_gate_inp_shexp.weight", [256], ggmlType: 0, offset: 13_888)
                .WithTensor("blk.0.ffn_gate_shexp.weight", [256, 4], ggmlType: 8, offset: 14_912)
                .Build());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabArchitectureResponse>(JsonOptions));
        AssertEx.Equal("detailed", body.Support);
        AssertEx.Equal("qwen35moe", body.Architecture);
        var block = body.Blocks.Single();
        AssertEx.Equal("linearAttention", block.Kind);
        // F32 norms, ssm_a and router (1 024 + 64 + 8 192 + 1 024), 32 Q4_K rows of 144 bytes, 32 Q8_0 blocks of 34.
        AssertEx.Equal(expected: 10_304L + 4_608 + 1_088, block.ByteSize!.Value);
        AssertEx.Equal("blk.0.ssm,blk.0.ffn,blk.0.norm", string.Join(',', block.Groups.Select(static group => group.ComponentId)));
        var ffn = block.Groups[1].Tensors.ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("model.language_model.layers.0.mlp.experts.gate_up_proj", ffn["blk.0.ffn_gate_exps.weight"].HfName);
        // One half of the fused HF tensor, kept as such though Q4_K also changed the values.
        AssertEx.Equal("sliced", ffn["blk.0.ffn_gate_exps.weight"].ValueRelation);
        AssertEx.Equal("Q4_K", ffn["blk.0.ffn_gate_exps.weight"].GgmlType);
        AssertEx.Equal("equal", ffn["blk.0.ffn_gate_inp.weight"].ValueRelation);
        AssertEx.Equal("model.language_model.layers.0.mlp.shared_expert_gate.weight", ffn["blk.0.ffn_gate_inp_shexp.weight"].HfName);
        AssertEx.Equal("quantized", ffn["blk.0.ffn_gate_shexp.weight"].ValueRelation);
        var template = body.Templates.Single();
        AssertEx.Equal("linearAttention", template.Kind);
        AssertEx.Equal("llama.cpp src/models/qwen35moe.cpp @ b12014", template.Source);
        AssertEx.True(template.Nodes.Any(static node => node.Id == "router") && template.Nodes.All(static node => node.Id != "ffn_gate"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var wire = json.RootElement.GetProperty("blocks")[0].GetProperty("groups")[1].GetProperty("tensors").EnumerateArray()
                       .Single(static tensor => tensor.GetProperty("ggufName").GetString() == "blk.0.ffn_gate_exps.weight");
        AssertEx.Equal("sliced", wire.GetProperty("valueRelation").GetString());
    }

    [Test]
    public async Task ARegisteredLlamaGguf_IsDescribedInDetail()
    {
        using var dir = new TempDirectory("xe-hf-test");
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path,
            new GgufHeaderBytesBuilder()
                .WithString("general.architecture", "llama")
                .WithUint32("llama.block_count", value: 1)
                .WithTensor("token_embd.weight", [256, 10], ggmlType: 12, offset: 0)
                .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 1_440)
                .WithTensor("blk.0.attn_q.weight", [256, 4], ggmlType: 12, offset: 2_464)
                .WithTensor("blk.0.attn_v.weight", [256, 4], ggmlType: 12, offset: 3_040)
                .WithTensor("output_norm.weight", [256], ggmlType: 0, offset: 3_616)
                .Build());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabArchitectureResponse>(JsonOptions));
        AssertEx.Equal("detailed", body.Support);
        AssertEx.Equal("llama", body.Architecture);
        var block = body.Blocks.Single();
        AssertEx.Equal("fullAttention", block.Kind);
        AssertEx.Equal("blk.0.attention,blk.0.norm", string.Join(',', block.Groups.Select(static group => group.ComponentId)));
        var attention = block.Groups[0].Tensors.ToDictionary(static tensor => tensor.GgufName, StringComparer.Ordinal);
        AssertEx.Equal("model.layers.0.self_attn.q_proj.weight", attention["blk.0.attn_q.weight"].HfName);
        // The converter permuted Q for NORM RoPE before quantizing, so the order change outlives Q4_K.
        AssertEx.Equal("reordered", attention["blk.0.attn_q.weight"].ValueRelation);
        AssertEx.Equal("quantized", attention["blk.0.attn_v.weight"].ValueRelation);
        AssertEx.Equal("model.layers.0.input_layernorm.weight", block.Groups[1].Tensors.Single().HfName);
        AssertEx.Equal("equal", block.Groups[1].Tensors.Single().ValueRelation);
        var template = body.Templates.Single();
        AssertEx.Equal("fullAttention", template.Kind);
        AssertEx.Equal("llama.cpp src/models/llama.cpp @ b12014", template.Source);
        AssertEx.Equal("model.embed_tokens.weight", body.NonBlock.Single(static group => group.ComponentId == "embedding").Tensors.Single().HfName);
    }

    [Test]
    public async Task TheDeclaredHyperparameters_ReachTheWire_AndAValuePastTheJavaScriptSafeRangeIsNull()
    {
        using var dir = new TempDirectory("xe-hf-test");
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path,
            new GgufHeaderBytesBuilder()
                .WithString("general.architecture", "qwen35")
                .WithUint64("general.parameter_count", value: 9_007_199_254_740_991)
                .WithUint32("qwen35.block_count", value: 1)
                .WithUint32("qwen35.attention.key_length", value: 256)
                .WithUint32("qwen35.full_attention_interval", value: 4)
                .WithUint32("qwen35.expert_count", value: 0)
                .WithUint64("qwen35.context_length", value: 9_007_199_254_740_992)
                .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 0)
                .Build());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var values = json.RootElement.GetProperty("hyperparameters");
        AssertEx.Equal(expected: 9_007_199_254_740_991L, values.GetProperty("declaredParamCount").GetInt64());
        AssertEx.Equal(expected: 256L, values.GetProperty("attentionKeyLength").GetInt64());
        AssertEx.Equal(expected: 4L, values.GetProperty("fullAttentionInterval").GetInt64());
        AssertEx.Equal(expected: 0L, values.GetProperty("expertCount").GetInt64());
        AssertEx.Equal(JsonValueKind.Null, values.GetProperty("contextLength").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, values.GetProperty("expertUsedCount").ValueKind);
    }

    [Test]
    public async Task AnUnreadableFile_CarriesNullHyperparameters()
    {
        using var dir = new TempDirectory("xe-hf-test");
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path, "this is plainly not a GGUF header"u8.ToArray());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("unreadable", json.RootElement.GetProperty("support").GetString());
        AssertEx.Equal(JsonValueKind.Null, json.RootElement.GetProperty("hyperparameters").ValueKind);
    }

    private static string Route(string encodedModelName) =>
        $"/api/local/v1/model-lab/architecture/{encodedModelName}";

    private static TestServerWebAppFactory CreateFactory(string registeredPath)
    {
        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        store.ResolveModelFilePathAsync(RegisteredModel, Arg.Any<CancellationToken>()).Returns(registeredPath);
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGgufModelStore>();
                services.AddSingleton(store);
            }
        };
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, string encodedModelName)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route(encodedModelName));
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
