namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatHubTests
{
    [Test]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Negotiate_WhenOriginIsUnsafe_ReturnsForbidden()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = CreateNegotiateRequest(factory);
        request.Headers.Remove("Origin");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task SendMessage_WhenAuthorized_StreamsEventsInOrder()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddScoped<INodeChatStreamService, DeterministicNodeChatStreamService>();
            }
        };
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();

        await connection.StartAsync().ConfigureAwait(false);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in connection.StreamAsync<ChatStreamEvent>("SendMessage",
                           new NodeChatStreamRequest(conversationId, "hello", MessageId: messageId, RequestId: requestId)).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        AssertEx.Equal(3, events.Count);
        AssertEx.Equal(ChatStreamEventTypes.AssistantStreaming, events[0].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, events[1].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, events[2].Type);
        AssertEx.True(events.All(streamEvent => streamEvent.ConversationId == conversationId));
        AssertEx.True(events.All(streamEvent => streamEvent.MessageId == messageId));
        AssertEx.True(events.All(streamEvent => streamEvent.RequestId == requestId));
    }

    private static HttpRequestMessage CreateNegotiateRequest(TestingWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private sealed class DeterministicNodeChatStreamService : INodeChatStreamService
    {
        public async IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var messageId = request.MessageId.GetValueOrDefault(Guid.NewGuid());
            var requestId = request.RequestId.GetValueOrDefault(Guid.NewGuid());
            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantStreaming,
                request.ConversationId,
                messageId,
                requestId,
                NodeChatMessageStatusValues.Streaming,
                0,
                1);

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantDelta,
                request.ConversationId,
                messageId,
                requestId,
                NodeChatMessageStatusValues.Streaming,
                1,
                2,
                "hi",
                Content: "hi");
            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantCompleted,
                request.ConversationId,
                messageId,
                requestId,
                NodeChatMessageStatusValues.Completed,
                2,
                3,
                Content: "hi");
        }
    }
}
