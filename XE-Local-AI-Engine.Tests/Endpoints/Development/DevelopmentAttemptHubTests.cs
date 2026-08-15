namespace XE_Local_AI_Engine.Tests.Endpoints.Development;

using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Endpoints.Development.V1;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("DevelopmentFeatureConfiguration")]
public sealed class DevelopmentAttemptHubTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AttemptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Test]
    public void JsonProtocol_SerializesLiveUpdateEnumsAsStrings()
    {
        var protocol = new JsonHubProtocol();
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("developmentAttemptUpdate",
            [
                Update("warning") with
                {
                    Kind = DevelopmentAttemptLiveUpdateKind.Warning,
                    WarningCategory = DevelopmentProgressWarningCategory.RepeatedTool
                }
            ]),
            writer);

        var payload = Encoding.UTF8.GetString(writer.WrittenSpan);
        AssertEx.Contains(payload, "\"kind\":\"Warning\"");
        AssertEx.Contains(payload, "\"role\":\"Coder\"");
        AssertEx.Contains(payload, "\"status\":\"Running\"");
        AssertEx.Contains(payload, "\"warningCategory\":\"RepeatedTool\"");
    }

    [Test]
    public async Task Negotiate_WhenOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, LocalApiRoutes.Development.Hub + "/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task SubscribeAsync_WhenProjectDoesNotOwnTask_RejectsSubscription()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns<Task<DevelopmentTaskAggregate>>(_ => throw new KeyNotFoundException("task is not on project"));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptDoesNotBelongToTask_RejectsSubscription()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns(DevelopmentEndpointTests.TaskAggregate(ProjectId, TaskId, [Attempt(Guid.NewGuid(), DevelopmentAttemptStatus.Running)]));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "does not belong");
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptIsTerminal_RejectsSubscription()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Succeeded));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "active Development attempt");
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptHasNoLiveBrokerState_RejectsSubscription()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "no active live stream");
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptIsActive_ReturnsLatestSnapshotAndWatermark()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        AssertEx.True(broker.Register(AttemptId));
        AssertEx.True(broker.TryPublish(Update("snapshot")));
        await using var connection = CreateConnection(factory);
        await connection.StartAsync().ConfigureAwait(false);

        var snapshot = await connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, snapshot.Watermark);
        AssertEx.Equal("snapshot", snapshot.Latest?.OutputDelta);
    }

    [Test]
    public async Task PublishAsync_WhenClientSubscribed_DeliversUpdateToAttemptGroup()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        AssertEx.True(broker.Register(AttemptId));
        await using var connection = CreateConnection(factory);
        var received = new TaskCompletionSource<DevelopmentAttemptLiveUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<DevelopmentAttemptLiveUpdate>("developmentAttemptUpdate", update => received.TrySetResult(update));
        await connection.StartAsync().ConfigureAwait(false);
        _ = await connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId).ConfigureAwait(false);

        var expected = Update("delivered");
        var publisher = factory.Services.GetRequiredService<IDevelopmentAttemptLiveEventPublisher>();
        await publisher.PublishAsync(expected).ConfigureAwait(false);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        AssertEx.Equal("delivered", actual.OutputDelta);
    }

    private static IDevelopmentManagementService ServiceReturning(DevelopmentAttemptSnapshot attempt)
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns(DevelopmentEndpointTests.TaskAggregate(ProjectId, TaskId, [attempt]));
        return service;
    }

    private static TestServerWebAppFactory EnabledFactory(IDevelopmentManagementService service) =>
        new()
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IDevelopmentManagementService>();
                services.AddSingleton(service);
            }
        };

    private static HubConnection CreateConnection(TestServerWebAppFactory factory) =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost" + LocalApiRoutes.Development.Hub, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                options.Headers.Add("Origin", "http://localhost");
            })
            .Build();

    private static DevelopmentAttemptSnapshot Attempt(Guid attemptId, DevelopmentAttemptStatus status) =>
        new(attemptId,
            TaskId,
            null,
            DevelopmentAttemptRole.Coder,
            "coder-model",
            "local",
            status,
            1,
            status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running ? null : 2,
            null,
            null,
            null,
            1);

    private static DevelopmentAttemptLiveUpdate Update(string output) =>
        new()
        {
            ProjectId = ProjectId,
            TaskId = TaskId,
            AttemptId = AttemptId,
            Kind = DevelopmentAttemptLiveUpdateKind.Output,
            Role = DevelopmentAttemptRole.Coder,
            Status = DevelopmentAttemptStatus.Running,
            ModelId = "coder-model",
            Provider = "local",
            OutputDelta = output
        };
}
