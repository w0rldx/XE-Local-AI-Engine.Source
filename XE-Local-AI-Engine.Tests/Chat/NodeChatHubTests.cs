namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatHubTests
{
    [Test]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
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
        await using var factory = new TestServerWebAppFactory();
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

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddScoped<INodeChatStreamService, DeterministicNodeChatStreamService>();
            }
        };
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
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

        AssertEx.Equal(expected: 3, events.Count);
        AssertEx.Equal(ChatStreamEventTypes.AssistantStreaming, events[0].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, events[1].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, events[2].Type);
        AssertEx.True(events.All(streamEvent => streamEvent.ConversationId == conversationId));
        AssertEx.True(events.All(streamEvent => streamEvent.MessageId == messageId));
        AssertEx.True(events.All(streamEvent => streamEvent.RequestId == requestId));
    }

    [Test]
    public async Task SendMessage_WhenContentExceedsTheCap_FailsLegiblyAndNeverReachesTheStreamService()
    {
        // The three halves of the message-size defect, pinned end to end over a real SignalR connection:
        // the send is rejected AT THE HUB (so nothing is persisted and the conversation cannot be poisoned), the client
        // receives a message naming both sizes instead of the generic "local-chat-stream-failed" stream failure, and the
        // rejection happens below the transport's own MaximumReceiveMessageSize so the connection survives it.
        var recorder = new RecordingNodeChatStreamService();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddSingleton<INodeChatStreamService>(recorder);
            }
        };
        await using var connection = CreateHubConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        // One byte past the shipped 256 KB default, and still well under the 512 KB transport ceiling.
        var oversized = new string(c: 'a', count: (256 * 1024) + 1);

        var exception = await AssertEx.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var _ in connection.StreamAsync<ChatStreamEvent>("SendMessage",
                               new NodeChatStreamRequest(Guid.NewGuid(), oversized)).ConfigureAwait(false))
            {
                // The stream must fault before it yields anything.
            }
        }).ConfigureAwait(false);

        AssertEx.Contains(exception.Message, "Your message is too large");
        AssertEx.Contains(exception.Message, "257 KB");
        AssertEx.Contains(exception.Message, "limit 256 KB");
        AssertEx.Contains(exception.Message, "Attach large documents as files instead.");
        AssertEx.False(recorder.Invoked, "an over-cap send must be rejected before the stream service persists the user turn");
        AssertEx.False(exception.Message.Contains("aaaa", StringComparison.Ordinal), "the rejection must not echo the message content");
    }

    /// <summary>
    ///     <c>RefuseUndeclaredWrites</c> is a server-set field: only the development-workflow runtime arms it, on the
    ///     requests it builds itself. It travels on the same record the hub forwards, so a client can put it on the
    ///     wire — and the hub clears it, because nothing arriving here is running a workflow node and a client-armed
    ///     rule could only make that client's own turn refuse.
    /// </summary>
    [Test]
    public async Task SendMessage_WhenAClientArmsTheWriteDeclarationFlag_ClearsItBeforeTheStreamService()
    {
        var recorder = new RecordingNodeChatStreamService();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddSingleton<INodeChatStreamService>(recorder);
            }
        };
        await using var connection = CreateHubConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        await foreach (var _ in connection.StreamAsync<ChatStreamEvent>("SendMessage",
                           new NodeChatStreamRequest(Guid.NewGuid(), "hello", RefuseUndeclaredWrites: true)).ConfigureAwait(false))
        {
            // The scrub is what this pins; the stream itself is the recorder's fixed single event.
        }

        AssertEx.True(recorder.Invoked, "the send itself is not rejected — only the field is dropped.");
        AssertEx.False(AssertEx.NotNull(recorder.LastRequest).RefuseUndeclaredWrites,
            "a client may not arm GRAPH-C4-2's runtime half on a turn no workflow node is driving.");
    }

    [Test]
    public async Task SendMessage_WhenTheOperatorLowersTheCap_RejectsAtTheConfiguredLimit()
    {
        var recorder = new RecordingNodeChatStreamService();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddSingleton<INodeChatStreamService>(recorder);
                services.Configure<SecurityOptions>(options => options.MaxMessageSizeKb = 1);
            }
        };
        await using var connection = CreateHubConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        var exception = await AssertEx.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var _ in connection.StreamAsync<ChatStreamEvent>("SendMessage",
                               new NodeChatStreamRequest(Guid.NewGuid(), new string(c: 'a', count: 1025))).ConfigureAwait(false))
            {
                // The stream must fault before it yields anything.
            }
        }).ConfigureAwait(false);

        AssertEx.Contains(exception.Message, "limit 1 KB");
        AssertEx.False(recorder.Invoked);
    }

    [Test]
    public async Task SendMessage_WhenContentIsExactlyAtTheCap_IsAccepted()
    {
        var recorder = new RecordingNodeChatStreamService();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddSingleton<INodeChatStreamService>(recorder);
                services.Configure<SecurityOptions>(options => options.MaxMessageSizeKb = 1);
            }
        };
        await using var connection = CreateHubConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        await foreach (var _ in connection.StreamAsync<ChatStreamEvent>("SendMessage",
                           new NodeChatStreamRequest(Guid.NewGuid(), new string(c: 'a', count: 1024))).ConfigureAwait(false))
        {
            // Drained; the assertion below is that the send reached the service at all.
        }

        AssertEx.True(recorder.Invoked, "a message exactly at the cap must reach the stream service");
    }

    private static HubConnection CreateHubConnection(TestServerWebAppFactory factory)
    {
        return new HubConnectionBuilder()
               .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
               {
                   options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                   options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                   options.Headers.Add("Origin", "http://localhost");
               })
               .Build();
    }

    private static HttpRequestMessage CreateNegotiateRequest(TestServerWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/chat/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    // Records whether the hub ever reached the send path. The stream service owns every write for a turn (it persists
    // the user message on its first await), so "not invoked" IS "nothing was written".
    private sealed class RecordingNodeChatStreamService : INodeChatStreamService
    {
        public bool Invoked { get; private set; }

        /// <summary>The request the hub actually handed down, which is not always the one the client put on the wire.</summary>
        public NodeChatStreamRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Invoked = true;
            LastRequest = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantCompleted,
                request.ConversationId,
                request.MessageId.GetValueOrDefault(Guid.NewGuid()),
                request.RequestId.GetValueOrDefault(Guid.NewGuid()),
                NodeChatMessageStatusValues.Completed,
                Sequence: 0,
                OccurredAtUtc: 1,
                Content: "ok");
        }
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
                Sequence: 0,
                OccurredAtUtc: 1);

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantDelta,
                request.ConversationId,
                messageId,
                requestId,
                NodeChatMessageStatusValues.Streaming,
                Sequence: 1,
                OccurredAtUtc: 2,
                "hi",
                Content: "hi");
            yield return new ChatStreamEvent(ChatStreamEventTypes.AssistantCompleted,
                request.ConversationId,
                messageId,
                requestId,
                NodeChatMessageStatusValues.Completed,
                Sequence: 2,
                OccurredAtUtc: 3,
                Content: "hi");
        }
    }
}
