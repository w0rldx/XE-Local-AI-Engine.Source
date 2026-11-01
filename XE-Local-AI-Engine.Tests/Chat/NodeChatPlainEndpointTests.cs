namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint contract for the per-conversation plain mode: the PATCH rides the Operator-gated conversation-mutation
///     surface, validates the custom prompt's length and round-trips through the read path.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatPlainEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task SetPlain_WhenNoBearerToken_ReturnsUnauthorized()
    {
        using var client = Factory.CreateClient();
        using var request = PlainRequest(Guid.NewGuid(), isPlain: true, prompt: null, authorize: false);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task SetPlain_Toggle_RoundTrips()
    {
        using var client = Factory.CreateClient();
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Plain API",
            UserId = null,
            CreatedAtUtc = 10
        });

        using var request = PlainRequest(conversation.ConversationId, isPlain: true, prompt: "Be brief.", authorize: true);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.True(document.RootElement.GetProperty("isPlain").GetBoolean(), "The response reflects the plain flag.");
        AssertEx.Equal("Be brief.", document.RootElement.GetProperty("plainSystemPrompt").GetString());

        var reloaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        AssertEx.True(reloaded.IsPlain, "The plain flag persists.");
        AssertEx.Equal("Be brief.", reloaded.PlainSystemPrompt);
    }

    [Test]
    public async Task SetPlain_WhenPromptOverCap_ReturnsBadRequest()
    {
        using var client = Factory.CreateClient();
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Plain cap",
            UserId = null,
            CreatedAtUtc = 10
        });

        using var request = PlainRequest(conversation.ConversationId,
            isPlain: true,
            prompt: new string('x', NodeChatSetConversationPlainRequest.MaxPlainSystemPromptLength + 1),
            authorize: true);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var reloaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        AssertEx.False(reloaded.IsPlain, "A rejected request leaves the conversation unchanged.");
    }

    [Test]
    public async Task SetPlain_OnAWorkSessionConversation_ReturnsBadRequestAndWritesNothing()
    {
        using var client = Factory.CreateClient();
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Plain work session",
            UserId = null,
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.WorkSession
        });

        using var request = PlainRequest(conversation.ConversationId, isPlain: true, prompt: null, authorize: true);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "Only an ordinary chat conversation can be plain.");
        var reloaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        AssertEx.False(reloaded.IsPlain, "A refused kind stays non-plain.");
    }

    [Test]
    public async Task SetPlain_WhenConversationMissing_ReturnsNotFound()
    {
        using var client = Factory.CreateClient();
        using var request = PlainRequest(Guid.NewGuid(), isPlain: true, prompt: null, authorize: true);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpRequestMessage PlainRequest(Guid conversationId, bool isPlain, string? prompt, bool authorize)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/local/v1/chat/conversations/{conversationId}/plain")
        {
            Content = JsonContent.Create(new
            {
                isPlain,
                plainSystemPrompt = prompt
            })
        };
        if (authorize)
        {
            Factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");
        }

        return request;
    }
}
