namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Conversations_WhenCreated_CanBeListedAndLoaded()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var createRequest = CreateJsonRequest(factory,
            HttpMethod.Post,
            "/api/local/v1/chat/conversations",
            new { Title = "API chat", UserId = "local-operator" });
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);
        var created = await ReadJsonAsync<NodeChatConversationResponse>(createResponse).ConfigureAwait(false);

        using var listRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/chat/conversations?limit=10");
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var listed = await ReadJsonAsync<ListNodeChatConversationsResponse>(listResponse).ConfigureAwait(false);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, $"/api/local/v1/chat/conversations/{created.ConversationId}");
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);
        var loaded = await ReadJsonAsync<NodeChatConversationResponse>(getResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AssertEx.Equal("API chat", created.Title);
        AssertEx.Contains(listed.Items.Select(static item => item.ConversationId), created.ConversationId);
        AssertEx.Equal(created.ConversationId, loaded.ConversationId);
        AssertEx.Empty(loaded.Messages);
    }

    [Test]
    public async Task Cancel_WhenCorrelationMatches_TerminalizesOnlyThatMessage()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var persistence = factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Cancel API", null, CreatedAtUtc: 10)).ConfigureAwait(false);
        var targetMessageId = Guid.NewGuid();
        var otherMessageId = Guid.NewGuid();
        var targetRequestId = Guid.NewGuid();
        var otherRequestId = Guid.NewGuid();

        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, targetMessageId, targetRequestId, CreatedAtUtc: 11)).ConfigureAwait(false);
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, otherMessageId, otherRequestId, CreatedAtUtc: 12)).ConfigureAwait(false);

        using var cancelRequest = CreateJsonRequest(factory,
            HttpMethod.Post,
            "/api/local/v1/chat/cancel",
            new
            {
                conversation.ConversationId,
                MessageId = targetMessageId,
                RequestId = targetRequestId
            });
        using var cancelResponse = await client.SendAsync(cancelRequest).ConfigureAwait(false);
        var cancelled = await ReadJsonAsync<NodeChatCancelMessageResponse>(cancelResponse).ConfigureAwait(false);
        var loaded = await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        AssertEx.True(cancelled.Cancelled);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, cancelled.Status);
        var loadedConversation = AssertEx.NotNull(loaded);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, loadedConversation.Messages.Single(message => message.MessageId == targetMessageId).Status);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, loadedConversation.Messages.Single(message => message.MessageId == otherMessageId).Status);
    }

    [Test]
    public async Task ConversationDelete_WhenConversationExists_HidesConversationFromGetAndList()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var persistence = factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Delete API", null, CreatedAtUtc: 20)).ConfigureAwait(false);

        using var deleteRequest = CreateRequest(factory, HttpMethod.Delete, $"/api/local/v1/chat/conversations/{conversation.ConversationId}");
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<NodeChatDeleteConversationResponse>(deleteResponse).ConfigureAwait(false);

        using var getRequest = CreateRequest(factory, HttpMethod.Get, $"/api/local/v1/chat/conversations/{conversation.ConversationId}");
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        using var listRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/chat/conversations");
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);
        var listed = await ReadJsonAsync<ListNodeChatConversationsResponse>(listResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal(conversation.ConversationId, deleted.ConversationId);
        AssertEx.False(deleted.Purged);
        AssertEx.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        AssertEx.False(listed.Items.Any(item => item.ConversationId == conversation.ConversationId));
    }

    private static HttpRequestMessage CreateJsonRequest<T>(TestingWebAppFactory factory, HttpMethod method, string uri, T content)
    {
        var request = CreateRequest(factory, method, uri);
        request.Content = JsonContent.Create(content);
        return request;
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
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
