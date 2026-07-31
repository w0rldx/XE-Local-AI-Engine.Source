namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="XE_Local_AI_Engine.Client.Hubs.PreviewWorkflowHub" /> tests: negotiate requires the operator token;
///     an event published through <see cref="IPreviewWorkflowEventPublisher" /> reaches only a connection subscribed to
///     that run's group (every payload carries the runId); and a disconnect cancels the runs owned by the connection.
/// </summary>
public sealed class PreviewWorkflowHubTests
{
    [Test]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/preview/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task PreviewHub_EventsCarryRunId_ScopedDelivery()
    {
        var runId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await using var factory = new TestingWebAppFactory();
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.Preview.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();

        var received = new TaskCompletionSource<PreviewWorkflowRunHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<PreviewWorkflowRunHubEvent>(PreviewWorkflowHubEvents.RunStarted, evt => received.TrySetResult(evt));

        await connection.StartAsync().ConfigureAwait(false);

        // Subscribe to this run's group, then publish — the event must arrive scoped to the group, carrying its runId.
        await connection.InvokeAsync("Subscribe", runId, -1L).ConfigureAwait(false);

        var publisher = factory.Services.GetRequiredService<IPreviewWorkflowEventPublisher>();
        await publisher.PublishRunAsync(new PreviewWorkflowRunHubEvent(PreviewWorkflowHubEvents.RunStarted, runId, NodeId: null, Output: null, Error: null, RequestId: null, OccurredAtUtc: 123L,
                           Seq: 0L))
                       .ConfigureAwait(false);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        AssertEx.Equal(runId, evt.RunId);
        AssertEx.Equal(PreviewWorkflowHubEvents.RunStarted, evt.EventType);
    }

    [Test]
    public async Task PreviewHub_Subscribe_ReplaysBufferedEvents_ToCallerOnly()
    {
        // The subscribe-after-publish race: buffer events for a run BEFORE any connection subscribes (mirrors a run
        // that already produced events, or already finished, by the time a client joins). Subscribe must join the
        // group AND replay every buffered event to the caller, each carrying its original method name + seq.
        var runId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var nodeEvent = new PreviewWorkflowNodeHubEvent(PreviewWorkflowHubEvents.NodeOutput, runId, "agent",
            Output: "hello", Error: null, OccurredAtUtc: 100L, Seq: 0L);
        var runEvent = new PreviewWorkflowRunHubEvent(PreviewWorkflowHubEvents.RunCompleted, runId, NodeId: null,
            Output: "done", Error: null, RequestId: null, OccurredAtUtc: 200L, Seq: 1L);

        var executionService = Substitute.For<IPreviewWorkflowExecutionService>();
        executionService.SnapshotBufferedEvents(runId, afterSeq: -1)
                        .Returns([
                            new PreviewWorkflowBufferedEvent(PreviewWorkflowHubEvents.NodeOutput, nodeEvent),
                            new PreviewWorkflowBufferedEvent(PreviewWorkflowHubEvents.RunCompleted, runEvent)
                        ]);

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowExecutionService>();
                services.AddSingleton(executionService);
            }
        };

        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.Preview.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();

        var receivedNode = new TaskCompletionSource<PreviewWorkflowNodeHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedRun = new TaskCompletionSource<PreviewWorkflowRunHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<PreviewWorkflowNodeHubEvent>(PreviewWorkflowHubEvents.NodeOutput, evt => receivedNode.TrySetResult(evt));
        _ = connection.On<PreviewWorkflowRunHubEvent>(PreviewWorkflowHubEvents.RunCompleted, evt => receivedRun.TrySetResult(evt));

        await connection.StartAsync().ConfigureAwait(false);

        // No prior publish — the ONLY source of these events is the join-then-replay path inside Subscribe. afterSeq
        // -1 is "I have seen nothing", the value a freshly loaded page sends.
        await connection.InvokeAsync("Subscribe", runId, -1L).ConfigureAwait(false);

        var gotNode = await receivedNode.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var gotRun = await receivedRun.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        AssertEx.Equal(PreviewWorkflowHubEvents.NodeOutput, gotNode.EventType);
        AssertEx.Equal(expected: 0L, gotNode.Seq, "the replayed node event must carry its original seq.");
        AssertEx.Equal(PreviewWorkflowHubEvents.RunCompleted, gotRun.EventType);
        AssertEx.Equal(expected: 1L, gotRun.Seq, "the replayed run event must carry its original seq.");
    }

    [Test]
    public async Task PreviewHub_Subscribe_FromSeq_ReplaysOnlyUnseenEvents_AndRegistersSubscriber()
    {
        // Reattach-after-reconnect: a client that already applied up to seq 0 resubscribes with afterSeq 0. The hub
        // must replay ONLY seq 1 (no duplicate of seq 0) and must register the connection as a live subscriber, which
        // is what keeps the run out of the abandoned-subscriber sweep.
        var runId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var seen = new PreviewWorkflowNodeHubEvent(PreviewWorkflowHubEvents.NodeOutput, runId, "agent",
            Output: "already-applied", Error: null, OccurredAtUtc: 100L, Seq: 0L);
        var unseen = new PreviewWorkflowRunHubEvent(PreviewWorkflowHubEvents.RunCompleted, runId, NodeId: null,
            Output: "done", Error: null, RequestId: null, OccurredAtUtc: 200L, Seq: 1L);

        var executionService = Substitute.For<IPreviewWorkflowExecutionService>();
        // A full replay (afterSeq -1) would return BOTH; the seq-filtered call returns only the unseen one. Wiring
        // both arrangements means the assertion below fails if the hub ignores afterSeq and asks for everything.
        executionService.SnapshotBufferedEvents(runId, -1L)
                        .Returns([
                            new PreviewWorkflowBufferedEvent(PreviewWorkflowHubEvents.NodeOutput, seen),
                            new PreviewWorkflowBufferedEvent(PreviewWorkflowHubEvents.RunCompleted, unseen)
                        ]);
        executionService.SnapshotBufferedEvents(runId, 0L)
                        .Returns([new PreviewWorkflowBufferedEvent(PreviewWorkflowHubEvents.RunCompleted, unseen)]);

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowExecutionService>();
                services.AddSingleton(executionService);
            }
        };

        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.Preview.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();

        var nodeEvents = new List<PreviewWorkflowNodeHubEvent>();
        var receivedRun = new TaskCompletionSource<PreviewWorkflowRunHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<PreviewWorkflowNodeHubEvent>(PreviewWorkflowHubEvents.NodeOutput, evt =>
        {
            lock (nodeEvents)
            {
                nodeEvents.Add(evt);
            }
        });
        _ = connection.On<PreviewWorkflowRunHubEvent>(PreviewWorkflowHubEvents.RunCompleted, evt => receivedRun.TrySetResult(evt));

        await connection.StartAsync().ConfigureAwait(false);
        await connection.InvokeAsync("Subscribe", runId, 0L).ConfigureAwait(false);

        var gotRun = await receivedRun.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        AssertEx.Equal(expected: 1L, gotRun.Seq, "the replay must deliver the event the client has not seen.");

        lock (nodeEvents)
        {
            AssertEx.Empty(nodeEvents);
        }

        executionService.Received().AddSubscriber(runId, Arg.Any<string>());
    }

    [Test]
    public async Task PreviewHub_Disconnect_DropsSubscriberFromEveryRun()
    {
        // The reload path: the page goes away, the connection drops, and the run's watcher set must lose that
        // connection so its abandoned-subscriber grace period starts. Without this the sweep can never fire and a
        // paused run holds its concurrency slot until the node restarts.
        var executionService = Substitute.For<IPreviewWorkflowExecutionService>();

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowExecutionService>();
                services.AddSingleton(executionService);
            }
        };

        var connection = new HubConnectionBuilder()
                         .WithUrl("http://localhost" + LocalApiRoutes.Preview.Hub, options =>
                         {
                             options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                             options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                             options.Headers.Add("Origin", "http://localhost");
                         })
                         .Build();

        await connection.StartAsync().ConfigureAwait(false);
        await connection.StopAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);

        await AssertEx.EventuallyAsync(() => executionService.ReceivedCalls()
                                                             .Any(c => c.GetMethodInfo().Name == nameof(IPreviewWorkflowExecutionService.RemoveSubscriberFromAllRuns)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [Test]
    public async Task PreviewHub_Disconnect_CancelsOwnedActiveRun()
    {
        var executionService = Substitute.For<IPreviewWorkflowExecutionService>();

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowExecutionService>();
                services.AddSingleton(executionService);
            }
        };

        var connection = new HubConnectionBuilder()
                         .WithUrl("http://localhost" + LocalApiRoutes.Preview.Hub, options =>
                         {
                             options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                             options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                             options.Headers.Add("Origin", "http://localhost");
                         })
                         .Build();

        await connection.StartAsync().ConfigureAwait(false);
        await connection.StopAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);

        // The hub's OnDisconnectedAsync must ask the execution service to cancel runs owned by the connection.
        await AssertEx.EventuallyAsync(() => executionService.ReceivedCalls()
                                                             .Any(c => c.GetMethodInfo().Name == nameof(IPreviewWorkflowExecutionService.CancelRunsForConnectionAsync)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
}
