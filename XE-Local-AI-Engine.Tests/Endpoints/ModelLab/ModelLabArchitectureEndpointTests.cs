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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        await using var factory = CreateFactory(dir.FilePath("tiny.gguf"));
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "org%2FUnknown-GGUF%3AQ4_K_M");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ARegisteredQwen35Gguf_IsDescribedInDetail()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
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
