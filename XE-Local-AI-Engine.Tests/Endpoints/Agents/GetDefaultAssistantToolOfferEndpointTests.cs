namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Integration)]
public sealed class GetDefaultAssistantToolOfferEndpointTests
{
    private const string Route = "/api/local/v1/agents/default-tool-offer";
    private const string CapableLocalModel = "capable-local";
    private const string CapableCloudModel = "capable-cloud";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Get_OnAToolCapableLocalModel_OffersTheLocalDataToolsAndAskUserButNoProfileOptInTool()
    {
        var offer = await GetOfferAsync($"?modelName={CapableLocalModel}");

        AssertEx.Equal(CapableLocalModel, offer.ModelName);
        AssertEx.Contains(offer.ToolNames, "search_knowledge_base");
        AssertEx.Contains(offer.ToolNames, "ask_user");
        AssertEx.Contains(offer.ToolNames, "web_search");
        AssertEx.False(offer.ToolNames.Contains("spawn_subagent", StringComparer.Ordinal), "spawn_subagent must not be offered");
        AssertEx.False(offer.ToolNames.Contains("run_python", StringComparer.Ordinal), "run_python must not be offered");
    }

    [Test]
    public async Task Get_WithNoModel_ResolvesTheNodeLocalDefault()
    {
        var offer = await GetOfferAsync(string.Empty);

        AssertEx.Equal(CapableLocalModel, offer.ModelName);
        AssertEx.Contains(offer.ToolNames, "search_knowledge_base");
    }

    [Test]
    public async Task Get_OnAModelOutsideTheToolCapableAllowlist_WithholdsTheCapableOnlyTools()
    {
        var offer = await GetOfferAsync("?modelName=local-not-allowlisted");

        AssertEx.NotEmpty(offer.ToolNames);
        AssertEx.False(offer.ToolNames.Contains("ask_user", StringComparer.Ordinal), "ask_user must not be offered");
        AssertEx.False(offer.ToolNames.Contains("search_knowledge_base", StringComparer.Ordinal), "search_knowledge_base must not be offered");
        AssertEx.False(offer.ToolNames.Contains("web_search", StringComparer.Ordinal), "web_search must not be offered");
    }

    [Test]
    public async Task Get_OnAModelThatDoesNotAdvertiseTools_OffersNothing()
    {
        var offer = await GetOfferAsync("?modelName=local-without-tools");

        AssertEx.Empty(offer.ToolNames);
    }

    [Test]
    public async Task Get_OnACloudModel_WithholdsTheWebAndLocalDataTools()
    {
        var offer = await GetOfferAsync($"?modelName={CapableCloudModel}");

        AssertEx.Contains(offer.ToolNames, "ask_user");
        AssertEx.False(offer.ToolNames.Contains("search_knowledge_base", StringComparer.Ordinal), "search_knowledge_base must not be offered");
        AssertEx.False(offer.ToolNames.Contains("web_search", StringComparer.Ordinal), "web_search must not be offered");
        AssertEx.False(offer.ToolNames.Contains("web_fetch", StringComparer.Ordinal), "web_fetch must not be offered");
    }

    [Test]
    public async Task Get_WithoutABearerToken_IsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<DefaultAssistantToolOfferResponse> GetOfferAsync(string query)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route + query);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<DefaultAssistantToolOfferResponse>(stream, JsonOptions));
    }

    private static TestServerWebAppFactory CreateFactory() =>
        new()
        {
            ConfigureAdditionalTestServices = static services =>
            {
                // Deterministic capability answers: the real resolver probes providers, so the result would depend on
                // what happens to be installed. Everything downstream of it is the real send-path composition.
                services.RemoveAll<IModelCapabilityResolver>();
                services.AddSingleton<IModelCapabilityResolver, NamedModelCapabilities>();
                services.RemoveAll<ILocalDefaultChatModelResolver>();
                services.AddSingleton<ILocalDefaultChatModelResolver, CapableLocalDefault>();
                services.RemoveAll<INodeRuntimeSettings>();
                services.AddSingleton(StubNodeRuntimeSettings.Create()
                                                             .WithToolCapableModels(CapableLocalModel, CapableCloudModel)
                                                             .WithWebAccessEnabled(webAccessEnabled: true)
                                                             .Build());
            }
        };

    /// <summary>Advertises tools unless the name says "without-tools"; cloud only when the name says "cloud".</summary>
    private sealed class NamedModelCapabilities : IModelCapabilityResolver
    {
        public Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false,
                model is not null && !model.Contains("without-tools", StringComparison.Ordinal),
                model?.Contains("cloud", StringComparison.Ordinal) == true));
    }

    private sealed class CapableLocalDefault : ILocalDefaultChatModelResolver
    {
        public Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(CapableLocalModel);
    }
}
