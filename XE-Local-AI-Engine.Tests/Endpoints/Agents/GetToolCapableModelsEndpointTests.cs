namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GetToolCapableModelsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetToolCapableModels_WhenOptionsContainDuplicate_ReturnsDistinctModels()
    {
        // LOW-1 regression: configuration list binding appends rather than replaces, so a repeated default model id
        // (the default is ["qwen3:8b"]) lands in the bound list twice. The endpoint must distinct the projection so
        // the response is a clean set rather than ["qwen3:8b","qwen3:8b"].
        await using var factory = CreateFactory("qwen3:8b", "qwen3:8b");
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/agents/tool-capable-models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ToolCapableModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(1, models.Models.Count);
        AssertEx.Equal("qwen3:8b", models.Models[0]);
    }

    [Test]
    public async Task GetToolCapableModels_WhenOptionsAreDistinct_ReturnsThemUnchanged()
    {
        await using var factory = CreateFactory("qwen3:8b", "llama3:8b");
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/agents/tool-capable-models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ToolCapableModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(2, models.Models.Count);
        AssertEx.Contains(models.Models, "qwen3:8b");
        AssertEx.Contains(models.Models, "llama3:8b");
    }

    private static TestingWebAppFactory CreateFactory(params string[] toolCapableModels)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.Configure<AgentHomeOptions>(options => options.ToolCapableModels = toolCapableModels);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
