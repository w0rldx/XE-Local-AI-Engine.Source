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
        await connection.InvokeAsync("Subscribe", runId).ConfigureAwait(false);

        var publisher = factory.Services.GetRequiredService<IPreviewWorkflowEventPublisher>();
        await publisher.PublishRunAsync(new PreviewWorkflowRunHubEvent(PreviewWorkflowHubEvents.RunStarted, runId, null, null, null, null, 123L))
                       .ConfigureAwait(false);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        AssertEx.Equal(runId, evt.RunId);
        AssertEx.Equal(PreviewWorkflowHubEvents.RunStarted, evt.EventType);
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
