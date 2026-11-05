namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>POST <c>model-lab/checkpoint-preflight</c>: operator only, a malformed pick is a 400, and a refused pick is a 200 carrying its code.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabEndpointTests
{
    private const string Route = "/api/local/v1/model-lab/checkpoint-preflight";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task Anonymous_Is401()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Route, new
        {
            repoId = "Qwen/Qwen3.8-27B",
            installedModelName = "org/Some-GGUF:Q4_K_M"
        });

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments("not-a-repo", "org/Some-GGUF:Q4_K_M")]
    [Arguments("Qwen/Qwen3.8-27B", "")]
    public async Task AMalformedPick_Is400(string repoId, string installedModelName)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = Request(factory, repoId, installedModelName);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task AnUnknownCheckpointOnDisk_Is404()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                baseArtifactId = Guid.NewGuid(),
                installedModelName = "org/Some-GGUF:Q4_K_M"
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ARefusedPick_Is200WithItsCode()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = Request(factory, "Qwen/Qwen3.8-27B", "org/Some-GGUF:Q4_K_M");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabCheckpointPreflightResponse>(JsonOptions));
        AssertEx.Equal(ModelLabCheckpointRefusal.NotTrainable, body.Refusal!.Value);
        AssertEx.Equal("synthetic refusal", body.RefusalMessage!);
    }

    private static TestServerWebAppFactory CreateFactory()
    {
        var store = Substitute.For<IBaseCheckpointStore>();
        store.ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns<BaseCheckpointManifest>(_ => throw new BaseCheckpointNotTrainableException("synthetic refusal"));
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IBaseCheckpointStore>();
                services.AddSingleton(store);
            }
        };
    }

    private static HttpRequestMessage Request(TestServerWebAppFactory factory, string repoId, string installedModelName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                repoId,
                installedModelName
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
