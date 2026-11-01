namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GET <c>models/{modelName}/gguf-inspection</c>: the file comes from the model registry only, an unregistered name is
///     a 404, and a registered GGUF is read by the real metadata reader into per-block groups.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GgufInspectionEndpointTests
{
    private const string RegisteredModel = "org/Tiny-GGUF:Q4_K_M";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Inspect_UnregisteredModel_Returns404()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        await using var factory = CreateFactory(dir.FilePath("tiny.gguf"));
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, "org%2FUnknown-GGUF%3AQ4_K_M");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Inspect_RegisteredModel_ReturnsMetadataAndBlockGroups()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("tiny.gguf");
        await File.WriteAllBytesAsync(path,
            new GgufHeaderBytesBuilder()
                .WithString("general.architecture", "qwen35")
                .WithUint32("qwen35.block_count", value: 2)
                .WithUint32("general.file_type", value: 15)
                .WithString("tokenizer.chat_template", "{{ messages }}")
                .WithTensor("token_embd.weight", [256, 10], ggmlType: 12, offset: 0)
                .WithTensor("blk.0.attn_norm.weight", [256], ggmlType: 0, offset: 1_440)
                .WithTensor("blk.1.attn_norm.weight", [256], ggmlType: 0, offset: 2_464)
                .WithTensor("blk.1.ffn_up.weight", [256, 4], ggmlType: 12, offset: 3_488)
                .WithTensor("output_norm.weight", [256], ggmlType: 0, offset: 4_064)
                .Build());
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, Uri.EscapeDataString(RegisteredModel));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelGgufInspectionResponse>(JsonOptions));
        AssertEx.Equal(RegisteredModel, body.ModelName);
        AssertEx.Null(body.ReadFailure);
        AssertEx.Equal("qwen35", body.Architecture!);
        AssertEx.Equal("Q4_K_M", body.FileType!);
        AssertEx.Equal("{{ messages }}", body.ChatTemplate!);
        AssertEx.Equal(expected: 5L, body.TensorCount);
        // The header declares no parameter_count, so the endpoint reports the summed element count of all five tensors.
        AssertEx.Equal(expected: 2_560L + 256 + 256 + 1_024 + 256, body.ParameterCount!.Value);
        AssertEx.Equal(expected: 2, body.Blocks.Count);
        AssertEx.Equal(expected: 1, body.Blocks[1].Index);
        AssertEx.Equal(expected: 2, body.Blocks[1].Tensors.Count);
        // 1 024 bytes of F32 norm plus four 144-byte Q4_K rows.
        AssertEx.Equal(expected: 1_600L, body.Blocks[1].ByteSize!.Value);
        AssertEx.Equal("token_embd.weight,output_norm.weight", string.Join(separator: ',', body.NonBlockTensors.Select(static tensor => tensor.Name)));
        AssertEx.Equal(expected: 1_440L + 1_024 + 1_600 + 1_024, body.TotalTensorBytes!.Value);
    }

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

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, string encodedModelName)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/models/{encodedModelName}/gguf-inspection");
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
