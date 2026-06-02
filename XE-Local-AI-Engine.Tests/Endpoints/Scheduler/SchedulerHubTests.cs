namespace XE_Local_AI_Engine.Tests.Endpoints.Scheduler;

using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     SchedulerHub tests: the negotiate endpoint requires the operator token like the other local
///     hubs, and a sanitized event published through <see cref="ISchedulerEventPublisher" /> reaches an authorized
///     connected client (proving the hub bridge end-to-end).
/// </summary>
public sealed class SchedulerHubTests
{
    [Test]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/scheduler/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task PublishDefinitionAsync_IsReceivedByAuthorizedClient()
    {
        var scheduledJobId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await using var factory = new TestingWebAppFactory();
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.Scheduler.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();

        var received = new TaskCompletionSource<SchedulerDefinitionHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<SchedulerDefinitionHubEvent>(
            SchedulerHubEvents.JobDefinitionChanged,
            evt => received.TrySetResult(evt));

        await connection.StartAsync().ConfigureAwait(false);

        // Publish through the host's hub-backed publisher (supersedes the no-op default in the Client host).
        var publisher = factory.Services.GetRequiredService<ISchedulerEventPublisher>();
        await publisher.PublishDefinitionAsync(
            new SchedulerDefinitionHubEvent(SchedulerHubEvents.JobDefinitionChanged, scheduledJobId, "created", 123L))
            .ConfigureAwait(false);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        AssertEx.Equal(scheduledJobId, evt.ScheduledJobId);
        AssertEx.Equal("created", evt.Action);
        AssertEx.Equal(SchedulerHubEvents.JobDefinitionChanged, evt.EventType);
    }
}
