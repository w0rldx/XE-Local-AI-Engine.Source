namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Endpoint contract for the pre-send context estimate: Operator-gated, the content-free window shape labelled
///     <c>PreSendEstimate</c>, 404 for a model the node does not know and 400 for an unparsable agent id.
/// </summary>
/// <remarks>Everything downstream of the faked model facts is the real send-path composition.</remarks>
[Category(TestCategories.Integration)]
public sealed class NodeChatContextEstimateEndpointTests
{
    private const string Route = "/api/local/v1/chat/context-estimate";
    private const string KnownModel = "capable-local";
    private const int KnownWindow = 32768;

    [Test]
    public async Task Get_ForAKnownModel_ReturnsThePreSendEstimateShape()
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal("PreSendEstimate", root.GetProperty("kind").GetString());
        AssertEx.Equal(KnownModel, root.GetProperty("modelId").GetString());
        AssertEx.Equal(KnownWindow, root.GetProperty("windowTokens").GetInt32());
        AssertEx.True(root.GetProperty("usableWindowTokens").GetInt32() > 0, "a known window leaves usable input budget");
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("providerInputTokens").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("trimmed").ValueKind);
        AssertEx.Equal(0, root.GetProperty("toolsWithheldCount").GetInt32());

        var estimated = root.GetProperty("estimated");
        AssertEx.True(estimated.GetProperty("systemPromptTokens").GetInt32() > 0, "the resolved prompt costs tokens");
        AssertEx.Equal(estimated.GetProperty("systemPromptTokens").GetInt32()
                       + estimated.GetProperty("toolSchemaTokens").GetInt32()
                       + estimated.GetProperty("toolTemplatePreambleTokens").GetInt32(),
            estimated.GetProperty("totalTokens").GetInt32());
        var toolNames = root.GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()).ToList();
        AssertEx.Contains(toolNames, "ask_user");
    }

    [Test]
    public async Task Get_WithLocalToolsOff_OffersNoTools()
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}&useLocalTools=false");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal(0, root.GetProperty("tools").GetArrayLength());
        var estimated = root.GetProperty("estimated");
        AssertEx.Equal(0, estimated.GetProperty("toolSchemaTokens").GetInt32());
        AssertEx.Equal(0, estimated.GetProperty("toolTemplatePreambleTokens").GetInt32());
    }

    [Test]
    public async Task Get_WithoutTheLocalToolsParameter_OffersTools()
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.True(document.RootElement.GetProperty("tools").GetArrayLength() > 0, "an absent toggle keeps the default: tools on");
    }

    [Test]
    public async Task Get_WithNumCtxAndMaxOutputTokens_AppliesBoth()
    {
        // The known model is not a launched GGUF, so the requested window is taken as is and the reserve widens.
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}&numCtx=16000&maxOutputTokens=5000");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(16000, document.RootElement.GetProperty("windowTokens").GetInt32());
        AssertEx.Equal(5000, document.RootElement.GetProperty("reservedOutputTokens").GetInt32());
    }

    [Test]
    [Arguments("numCtx=lots")]
    [Arguments("maxOutputTokens=1.5")]
    public async Task Get_WithAnUnparsableOverride_ReturnsBadRequest(string query)
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}&{query}");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Get_ForAModelTheNodeDoesNotKnow_ReturnsNotFound()
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, "?modelName=nobody-knows-me");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Get_WithAnUnparsableAgentId_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        using var response = await SendAsync(factory, $"?modelName={KnownModel}&agentId=not-a-guid");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Get_WithoutABearerToken_IsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Route}?modelName={KnownModel}");
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string query)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route + query);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private static TestServerWebAppFactory CreateFactory() =>
        new()
        {
            ConfigureAdditionalTestServices = static services =>
            {
                // Deterministic model facts: the real resolvers probe providers, so the answer would depend on what is
                // installed. The turn resolution, tool-offer gate, prompt and estimator are the real registrations.
                services.RemoveAll<IModelCapabilityResolver>();
                services.AddSingleton<IModelCapabilityResolver, CapableModels>();
                services.RemoveAll<ILocalModelDetailsResolver>();
                services.AddSingleton<ILocalModelDetailsResolver, KnownModelDetails>();
                services.RemoveAll<INodeRuntimeSettings>();
                services.AddSingleton(StubNodeRuntimeSettings.Create()
                                                             .WithEnableTools(true)
                                                             .WithToolCapableModels(KnownModel)
                                                             .Build());
            }
        };

    private sealed class CapableModels : IModelCapabilityResolver
    {
        public Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: true, IsCloud: false));
    }

    private sealed class KnownModelDetails : ILocalModelDetailsResolver
    {
        public Task<LocalModelDetailsResolution> ResolveAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalModelDetailsResolution>(string.Equals(modelName, KnownModel, StringComparison.Ordinal)
                ? new LocalModelDetailsResolution.Ollama(new OllamaModelDetails
                {
                    MaxContextTokens = KnownWindow,
                    Capabilities = ["completion", "tools"]
                })
                : new LocalModelDetailsResolution.NoLocalDetails());
    }
}
