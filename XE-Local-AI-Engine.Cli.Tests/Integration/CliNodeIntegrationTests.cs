namespace XE_Local_AI_Engine.Cli.Tests.Integration;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Cli.Tests.Testing;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Tests.Testing;
using Server = XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Drives the CLI's discovery-free stack (auth, REST, hub transport) against a real in-process node. The chat stream
///     service and the resume registry are scripted, so the hub, its JSON, auth and the folding are the real thing.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CliNodeIntegrationTests
{
    [Test]
    public async Task TheCli_SignsInInBodyModeListsCreatesAndStreamsATurnToItsTerminal()
    {
        var streams = Substitute.For<Server.INodeChatStreamService>();
        streams.SendMessageAsync(Arg.Any<Server.NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
               .Returns(call => PlayAsync(Turn(call.Arg<Server.NodeChatStreamRequest>(),
                       Frame(Server.ChatStreamEventTypes.AssistantStreaming),
                       Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "Hel", offset: 0),
                       Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "lo", offset: 3),
                       Frame(Server.ChatStreamEventTypes.AssistantCompleted, content: "Hello")),
                   holdOpen: false,
                   call.Arg<CancellationToken>()));
        await using var node = await TestNode.StartAsync(services =>
        {
            services.RemoveAll<Server.INodeChatStreamService>();
            services.AddSingleton(streams);
        });
        await node.SetUpAdminAsync();
        using var http = NodeHttp.CreateClient(node.ConnectionOptions);
        using var auth = new NodeAuthClient(http, node.ConnectionOptions.Origin, TimeProvider.System, tokenFilePath: null, passwordProvider: null);

        var status = await auth.GetStatusAsync(CancellationToken.None);
        await auth.LoginAsync(TestNode.AdminPassword, TestNode.AdminEmail, CancellationToken.None);
        var api = new NodeApiClient(http, auth);
        var created = await api.CreateConversationAsync(new CreateConversationRequest
        {
            Title = "From the terminal"
        }, CancellationToken.None);
        var listed = await api.ListConversationsAsync(includeArchived: false, limit: 50, CancellationToken.None);
        await using var session = await ChatSession.ConnectAsync(node.ConnectionOptions, auth, api, TimeProvider.System, CancellationToken.None);
        var agentId = Guid.NewGuid();
        var events = await session.SendAsync(new NodeChatStreamRequest
                                  {
                                      ConversationId = created.ConversationId,
                                      Content = "hi",
                                      AgentDefinitionId = agentId,
                                      Model = "a-model"
                                  }, CancellationToken.None)
                                  .ToListAsync();
        var cancel = await AssertEx.ThrowsAsync<NodeApiException>(() => session.CancelAsync(events[^1], CancellationToken.None));

        AssertEx.False(status.SetupRequired);
        AssertEx.Contains(listed.Items, item => item.ConversationId == created.ConversationId && item.Title == "From the terminal");
        AssertEx.Equal("Status,Delta,Delta,Completed", string.Join(',', events.Select(static item => item.Kind)));
        AssertEx.Equal("Hello", events[^1].Text);
        var sent = (Server.NodeChatStreamRequest)streams.ReceivedCalls().Single().GetArguments()[0]!;
        AssertEx.Equal(created.ConversationId, sent.ConversationId);
        AssertEx.Equal("hi", sent.Content);
        AssertEx.Equal(agentId, sent.AgentDefinitionId);
        AssertEx.Equal("a-model", sent.Model);
        AssertEx.Equal(sent.MessageId, events[^1].MessageId);
        AssertEx.Equal(sent.RequestId, events[^1].RequestId);

        // The stub persisted no message, so a well-formed cancel reaches the correlation lookup and answers 404, not 400.
        AssertEx.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Test]
    public async Task Send_WhenADeltaSkipsAhead_ResumesThroughResumeMessageAndFoldsTheSnapshot()
    {
        var conversationId = Guid.NewGuid();
        Guid? invocationId = null;
        var streams = Substitute.For<Server.INodeChatStreamService>();
        streams.SendMessageAsync(Arg.Any<Server.NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   var request = call.Arg<Server.NodeChatStreamRequest>();
                   invocationId = request.RequestId;
                   return PlayAsync(Turn(request,
                           Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "Hel", offset: 0),
                           Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "XX", offset: 7)),
                       holdOpen: true,
                       call.Arg<CancellationToken>());
               });
        var registry = Substitute.For<Server.IInvocationResumeRegistry>();
        registry.ResumeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => PlayAsync(Resumed(conversationId,
                        call.Arg<Guid>(),
                        Frame(Server.ChatStreamEventTypes.AssistantSnapshot, content: "Hello wor"),
                        Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "ld", offset: 9),
                        Frame(Server.ChatStreamEventTypes.AssistantCompleted, content: "Hello world")),
                    holdOpen: false,
                    call.Arg<CancellationToken>()));
        await using var node = await StartScriptedNodeAsync(streams, registry);
        await using var cli = await CliStack.SignInAsync(node, TimeProvider.System);

        var events = await cli.Session.SendAsync(new NodeChatStreamRequest
        {
            ConversationId = conversationId,
            Content = "hi"
        }, CancellationToken.None).ToListAsync();

        AssertEx.Equal("Delta,Snapshot,Delta,Completed", string.Join(',', events.Select(static item => item.Kind)));
        AssertEx.Equal("Hello world", events[^1].Text);
        AssertEx.True(invocationId.HasValue && events.All(item => item.RequestId == invocationId), "every event carries the send's invocation id");
        _ = registry.Received(1).ResumeAsync(invocationId!.Value, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResumeConversation_SurfacesAReplayedApprovalOnceAndEndsWithTheTerminal()
    {
        var invocationId = Guid.NewGuid();
        var registry = Substitute.For<Server.IInvocationResumeRegistry>();
        await using var node = await StartScriptedNodeAsync(Substitute.For<Server.INodeChatStreamService>(), registry);
        await using var cli = await CliStack.SignInAsync(node, TimeProvider.System);
        var conversation = await cli.Api.CreateConversationAsync(new CreateConversationRequest(), CancellationToken.None);
        registry.TryGetLiveInvocationIdForConversation(conversation.ConversationId).Returns(invocationId);
        registry.ResumeAsync(invocationId, Arg.Any<CancellationToken>())
                .Returns(call => PlayAsync(Resumed(conversation.ConversationId,
                        invocationId,
                        Frame(Server.ChatStreamEventTypes.ApprovalRequested, approvalRequestId: "approval-1"),
                        Frame(Server.ChatStreamEventTypes.AssistantSnapshot, content: "Waiting"),
                        Frame(Server.ChatStreamEventTypes.ApprovalRequested, approvalRequestId: "approval-1"),
                        Frame(Server.ChatStreamEventTypes.AssistantCancelled, content: "Waiting")),
                    holdOpen: false,
                    call.Arg<CancellationToken>()));

        var events = await cli.Session.ResumeConversationAsync(conversation.ConversationId, CancellationToken.None).ToListAsync();
        var nothingLive = await cli.Session.ResumeConversationAsync(Guid.NewGuid(), CancellationToken.None).ToListAsync();

        AssertEx.Equal("ApprovalRequested,Snapshot,Cancelled", string.Join(',', events.Select(static item => item.Kind)));
        AssertEx.Equal("approval-1", events[0].Source.ApprovalRequestId);
        AssertEx.Equal(invocationId, events[^1].RequestId);
        AssertEx.Empty(nothingLive);
    }

    [Test]
    public async Task ResumeConversation_WhenTheFirstFrameIsAReconcile_SettlesFromThePersistedMessage()
    {
        var conversationId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var registry = Substitute.For<Server.IInvocationResumeRegistry>();
        registry.TryGetLiveInvocationIdForConversation(conversationId).Returns(invocationId);
        registry.ResumeAsync(invocationId, Arg.Any<CancellationToken>())
                .Returns(call => PlayAsync(Resumed(conversationId, invocationId, Frame(Server.ChatStreamEventTypes.AssistantReconcile)),
                    holdOpen: false,
                    call.Arg<CancellationToken>()));
        await using var node = await StartScriptedNodeAsync(Substitute.For<Server.INodeChatStreamService>(), registry);
        var persisted = $$"""
                          {"conversationId":"{{conversationId:D}}","messages":[{"messageId":"{{assistantMessageId:D}}","conversationId":"{{conversationId:D}}","requestId":"{{invocationId:D}}","role":"assistant","content":"Persisted answer","status":"completed"}]}
                          """;
        var restOrigin = new Uri("http://127.0.0.1:5123/");
        using var rest = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/login" => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
            _ when request.Path == $"/api/local/v1/chat/conversations/{conversationId:D}" => ScriptedHttpHandler.Json(HttpStatusCode.OK, persisted),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var restHttp = NodeHttp.CreateClient(new NodeConnectionOptions
        {
            Origin = restOrigin,
            HandlerFactory = () => rest
        });
        using var restAuth = new NodeAuthClient(restHttp, restOrigin, TimeProvider.System, tokenFilePath: null, _ => Task.FromResult<string?>("pw"));
        await using var cli = await CliStack.SignInAsync(node, TimeProvider.System, new NodeApiClient(restHttp, restAuth));

        var events = await cli.Session.ResumeConversationAsync(conversationId, CancellationToken.None).ToListAsync();

        AssertEx.Equal("Completed", string.Join(',', events.Select(static item => item.Kind)));
        var terminal = events[0];
        AssertEx.Equal("Persisted answer", terminal.Text);
        AssertEx.Equal(assistantMessageId, terminal.MessageId);
        AssertEx.Equal(invocationId, terminal.RequestId);
        _ = registry.Received(1).ResumeAsync(invocationId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Send_WhenTheStreamGoesSilent_TheWatchdogEndsItOnTheInjectedClock()
    {
        var streams = Substitute.For<Server.INodeChatStreamService>();
        streams.SendMessageAsync(Arg.Any<Server.NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
               .Returns(call => PlayAsync(Turn(call.Arg<Server.NodeChatStreamRequest>(), Frame(Server.ChatStreamEventTypes.AssistantStreaming)),
                   holdOpen: true,
                   call.Arg<CancellationToken>()));
        await using var node = await StartScriptedNodeAsync(streams, registry: null);
        var time = new ManualTimeProvider();
        await using var cli = await CliStack.SignInAsync(node, time);
        await using var stream = cli.Session.SendAsync(new NodeChatStreamRequest
        {
            ConversationId = Guid.NewGuid(),
            Content = "hi"
        }, CancellationToken.None).GetAsyncEnumerator();

        AssertEx.True(await stream.MoveNextAsync());
        var first = stream.Current;
        var next = stream.MoveNextAsync().AsTask();
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), "the inter-chunk deadline is armed");
        await AssertEx.StaysIncompleteAsync(next, "the stream waits while the deadline has not passed");
        time.Advance(ChatStreamWatchdog.InterChunkTimeout + TimeSpan.FromSeconds(1));
        var tripped = await AssertEx.ThrowsAsync<ChatStreamWatchdogException>(() => next);

        AssertEx.Equal(CliStreamEventKind.Status, first.Kind);
        AssertEx.False(tripped.NoFirstFrame);
    }

    [Test]
    public async Task RenewAccessToken_RotatesThroughTheRefreshHeaderOnTheRealNode()
    {
        await using var node = await TestNode.StartAsync();
        await node.SetUpAdminAsync();
        using var http = NodeHttp.CreateClient(node.ConnectionOptions);
        using var auth = new NodeAuthClient(http, node.ConnectionOptions.Origin, TimeProvider.System, tokenFilePath: null, passwordProvider: null);
        await auth.LoginAsync(TestNode.AdminPassword, email: null, CancellationToken.None);
        var first = await auth.GetAccessTokenAsync(CancellationToken.None);

        var renewed = await auth.RenewAccessTokenAsync(first, CancellationToken.None);
        var models = await new NodeApiClient(http, auth).ListRunningModelsAsync(CancellationToken.None);

        AssertEx.NotEqual(first, renewed);
        AssertEx.Equal(renewed, await auth.GetAccessTokenAsync(CancellationToken.None));
        AssertEx.NotNull(models.Items);
    }

    private static Task<TestNode> StartScriptedNodeAsync(Server.INodeChatStreamService streams, Server.IInvocationResumeRegistry? registry) =>
        TestNode.StartAsync(services =>
        {
            services.RemoveAll<Server.INodeChatStreamService>();
            services.AddSingleton(streams);
            if (registry is not null)
            {
                services.RemoveAll<Server.IInvocationResumeRegistry>();
                services.AddSingleton(registry);
            }
        });

    private static async IAsyncEnumerable<Server.ChatStreamEvent> PlayAsync(IEnumerable<Server.ChatStreamEvent> frames,
        bool holdOpen,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            yield return frame;
        }

        if (holdOpen)
        {
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        }
    }

    private static List<Server.ChatStreamEvent> Turn(Server.NodeChatStreamRequest request, params Server.ChatStreamEvent[] frames) =>
        frames.Select((frame, index) => Restamp(frame, request.ConversationId, request.MessageId!.Value, request.RequestId!.Value, index)).ToList();

    // A resume stamps the invocation id as the message id and numbers its frames from zero.
    private static List<Server.ChatStreamEvent> Resumed(Guid conversationId, Guid invocationId, params Server.ChatStreamEvent[] frames) =>
        frames.Select((frame, index) => Restamp(frame, conversationId, invocationId, invocationId, index)).ToList();

    private static Server.ChatStreamEvent Restamp(Server.ChatStreamEvent frame, Guid conversationId, Guid messageId, Guid requestId, long? sequence = null) =>
        frame with
        {
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Sequence = sequence ?? frame.Sequence
        };

    private static Server.ChatStreamEvent Frame(string type, string? delta = null, long? offset = null, string? content = null, string? approvalRequestId = null) =>
        new()
        {
            Type = type,
            ConversationId = Guid.Empty,
            MessageId = Guid.Empty,
            RequestId = Guid.Empty,
            Status = type switch
            {
                Server.ChatStreamEventTypes.AssistantCompleted => Server.NodeChatMessageStatusValues.Completed,
                Server.ChatStreamEventTypes.AssistantCancelled => Server.NodeChatMessageStatusValues.Cancelled,
                _ => Server.NodeChatMessageStatusValues.Streaming
            },
            Sequence = 0,
            OccurredAtUtc = 1,
            Delta = delta,
            ContentOffset = offset,
            ReasoningOffset = offset is null ? null : 0,
            Content = content,
            ApprovalRequestId = approvalRequestId
        };

    /// <summary>The CLI objects a command builds once it is signed in to a node.</summary>
    private sealed class CliStack : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly NodeAuthClient _auth;

        private CliStack(HttpClient http, NodeAuthClient auth, NodeApiClient api, ChatSession session)
        {
            _http = http;
            _auth = auth;
            Api = api;
            Session = session;
        }

        public NodeApiClient Api { get; }

        public ChatSession Session { get; }

        /// <param name="sessionApi">The REST client the session settles through; the node's own when null.</param>
        public static async Task<CliStack> SignInAsync(TestNode node, TimeProvider sessionTime, NodeApiClient? sessionApi = null)
        {
            await node.SetUpAdminAsync();
            var http = NodeHttp.CreateClient(node.ConnectionOptions);
            var auth = new NodeAuthClient(http, node.ConnectionOptions.Origin, TimeProvider.System, tokenFilePath: null, passwordProvider: null);
            await auth.LoginAsync(TestNode.AdminPassword, email: null, CancellationToken.None);
            var api = new NodeApiClient(http, auth);
            return new CliStack(http, auth, api, await ChatSession.ConnectAsync(node.ConnectionOptions, auth, sessionApi ?? api, sessionTime, CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _auth.Dispose();
            _http.Dispose();
        }
    }
}
