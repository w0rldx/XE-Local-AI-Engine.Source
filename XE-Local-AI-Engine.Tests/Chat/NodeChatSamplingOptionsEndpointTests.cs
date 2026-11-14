namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint contract for a conversation's own sampling settings: the Operator-gated PATCH stores them, the
///     conversation read returns them, null clears them, every knob is bounded, and only an ordinary chat takes them.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatSamplingOptionsEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Patch_WhenNoBearerToken_ReturnsUnauthorized()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, Route(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
                samplingOptions = new
                {
                    temperature = 0.5
                }
            })
        };

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Patch_StoresTheSettings_TheConversationReadReturnsThem_AndNullClearsThem()
    {
        var conversationId = await CreateConversationAsync(NodeConversationKind.Chat);

        using (var stored = await PatchAsync(conversationId, new
               {
                   samplingOptions = new
                   {
                       temperature = 0.4,
                       topK = 20,
                       seed = "42",
                       stop = new[]
                       {
                           "</end>"
                       }
                   }
               }))
        {
            AssertEx.Equal(HttpStatusCode.OK, stored.StatusCode);
            var returned = (await ReadJsonAsync(stored)).GetProperty("samplingOptions");
            AssertEx.Equal(0.4, Math.Round(returned.GetProperty("temperature").GetDouble(), 3));
            AssertEx.Equal("42", returned.GetProperty("seed").GetString());
        }

        using (var read = await GetAsync(conversationId))
        {
            var reloaded = (await ReadJsonAsync(read)).GetProperty("samplingOptions");
            AssertEx.Equal(20, reloaded.GetProperty("topK").GetInt32());
            AssertEx.Equal("</end>", reloaded.GetProperty("stop")[0].GetString());
            AssertEx.Equal(JsonValueKind.Null, reloaded.GetProperty("topP").ValueKind, "An unset knob reads back as null.");
        }

        using var cleared = await PatchAsync(conversationId, new
        {
            samplingOptions = (object?)null
        });
        AssertEx.Equal(HttpStatusCode.OK, cleared.StatusCode);
        AssertEx.Equal(JsonValueKind.Null, (await ReadJsonAsync(cleared)).GetProperty("samplingOptions").ValueKind);
    }

    [Test]
    [Arguments("temperature", "2.5")]
    [Arguments("temperature", "-0.1")]
    [Arguments("topP", "1.5")]
    [Arguments("topK", "0")]
    [Arguments("topK", "201")]
    [Arguments("minP", "-0.5")]
    [Arguments("maxOutputTokens", "0")]
    [Arguments("maxOutputTokens", "131073")]
    [Arguments("repeatPenalty", "2.5")]
    [Arguments("repeatLastN", "-2")]
    [Arguments("repeatLastN", "513")]
    [Arguments("presencePenalty", "-2.5")]
    [Arguments("frequencyPenalty", "2.5")]
    [Arguments("numCtx", "511")]
    [Arguments("numCtx", "131073")]
    [Arguments("seed", "\"-2\"")]
    [Arguments("seed", "\"2147483648\"")]
    [Arguments("seed", "\"not-a-number\"")]
    [Arguments("stop", "[\"\"]")]
    [Arguments("stop", "[null]")]
    [Arguments("stop", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\",\"l\",\"m\",\"n\",\"o\",\"p\",\"q\"]")]
    public async Task Patch_WithAKnobOutsideItsRange_ReturnsBadRequestAndStoresNothing(string knob, string jsonValue)
    {
        var conversationId = await CreateConversationAsync(NodeConversationKind.Chat);

        using var response = await PatchRawAsync(conversationId, $$$"""{"samplingOptions":{"{{{knob}}}":{{{jsonValue}}}}}""");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"{knob}={jsonValue} must be refused.");
        var stored = await Factory.Services.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(conversationId);
        AssertEx.Null(AssertEx.NotNull(stored).SamplingOptions);
    }

    [Test]
    [Arguments("temperature", "2")]
    [Arguments("topK", "1")]
    [Arguments("repeatLastN", "-1")]
    [Arguments("numCtx", "131072")]
    [Arguments("seed", "\"-1\"")]
    [Arguments("seed", "\"2147483647\"")]
    public async Task Patch_WithAKnobOnItsBound_IsStored(string knob, string jsonValue)
    {
        var conversationId = await CreateConversationAsync(NodeConversationKind.Chat);

        using var response = await PatchRawAsync(conversationId, $$$"""{"samplingOptions":{"{{{knob}}}":{{{jsonValue}}}}}""");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"{knob}={jsonValue} is inside the range.");
    }

    [Test]
    public async Task OpenApi_PublishesNoBoundsOnTheSharedSettingsSchema_SoAnOlderChatAlwaysLoads()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json");
        var schema = (await ReadJsonAsync(response)).GetProperty("components").GetProperty("schemas")
                                                    .GetProperty("XE_Local_AI_EngineClientEndpointsLocalChatV1NodeChatSamplingOptionsDto");

        var bounded = schema.GetProperty("properties").EnumerateObject()
                            .Where(static property => property.Value.TryGetProperty("minimum", out _) || property.Value.TryGetProperty("maximum", out _)
                                                                                                      || property.Value.TryGetProperty("maxItems", out _))
                            .Select(static property => property.Name)
                            .ToList();
        AssertEx.Empty(bounded, "The conversation response shares this schema; a published bound would make the browser refuse a chat stored under a wider one.");
    }

    [Test]
    [Arguments(NodeConversationKind.WorkSession)]
    [Arguments(NodeConversationKind.Integration)]
    public async Task Patch_OnAConversationThatIsNotAChat_ReturnsBadRequestAndStoresNothing(string kind)
    {
        var conversationId = await CreateConversationAsync(kind);

        using var response = await PatchAsync(conversationId, new
        {
            samplingOptions = new
            {
                temperature = 0.5
            }
        });

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await Factory.Services.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(conversationId);
        AssertEx.Null(AssertEx.NotNull(stored).SamplingOptions);
    }

    [Test]
    public async Task Patch_WhenConversationMissing_ReturnsNotFound()
    {
        using var response = await PatchAsync(Guid.NewGuid(), new
        {
            samplingOptions = new
            {
                temperature = 0.5
            }
        });

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Route(Guid conversationId) =>
        $"/api/local/v1/chat/conversations/{conversationId}/sampling-options";

    private async Task<Guid> CreateConversationAsync(string kind)
    {
        var conversation = await Factory.Services.GetRequiredService<INodeChatPersistenceService>().CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Sampling API",
            UserId = null,
            CreatedAtUtc = 10,
            Kind = kind
        });
        return conversation.ConversationId;
    }

    private Task<HttpResponseMessage> PatchAsync(Guid conversationId, object body) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Patch, Route(conversationId))
        {
            Content = JsonContent.Create(body)
        });

    private Task<HttpResponseMessage> PatchRawAsync(Guid conversationId, string json) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Patch, Route(conversationId))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    private Task<HttpResponseMessage> GetAsync(Guid conversationId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/chat/conversations/{conversationId}"));

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            using var client = Factory.CreateClient();
            Factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");
            return await client.SendAsync(request);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
