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
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Endpoints.Development.V1;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("DevelopmentFeatureConfiguration")]
[Category(TestCategories.Integration)]
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

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    ///     A missing project or task must say so, not arrive as SignalR's generic invocation error.
    /// </summary>
    /// <remarks>
    ///     The store throws <c>DevelopmentNotFoundException</c> for this miss. Every other subscribing hub translates
    ///     its own not-found family into a <c>HubException</c> carrying a message; this hub did not, so the client saw
    ///     the same opaque text for a wrong id as for a defect. Asserting the message is what makes the translation
    ///     observable — a plain <c>ThrowsAsync&lt;HubException&gt;</c> passes either way.
    /// </remarks>
    [Test]
    public async Task SubscribeAsync_WhenProjectDoesNotOwnTask_RejectsSubscriptionWithTheNotFoundMessage()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns<Task<DevelopmentTaskAggregate>>(_ => throw new DevelopmentNotFoundException("task is not on project"));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "was not found");
    }

    /// <summary>
    ///     The translation is typed: an unrelated defect must NOT be dressed up as a missing resource.
    /// </summary>
    [Test]
    public async Task SubscribeAsync_WhenTheServiceFaultsForAnotherReason_DoesNotReportItAsNotFound()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns<Task<DevelopmentTaskAggregate>>(_ => throw new InvalidOperationException("an unrelated defect"));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.False(exception.Message.Contains("was not found", StringComparison.Ordinal),
            "A defect that is not a missing resource must not be reported as one.");
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptDoesNotBelongToTask_RejectsSubscription()
    {
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns(DevelopmentEndpointTests.TaskAggregate(ProjectId, TaskId, [Attempt(Guid.NewGuid(), DevelopmentAttemptStatus.Running)]));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "does not belong");
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptIsTerminal_RejectsSubscription()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Succeeded));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "active Development attempt");
    }

    [Test]
    public async Task SubscribeAsync_WhenTheTaskValidatesItsSucceededCoderAttempt_SubscribesToTheValidationStream()
    {
        var service = ServiceReturning(DevelopmentTaskStatus.Validation,
            Attempt(Guid.NewGuid(), DevelopmentAttemptStatus.Failed),
            Attempt(AttemptId, DevelopmentAttemptStatus.Succeeded));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        AssertEx.True(broker.Register(AttemptId));
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var snapshot = await connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId);

        AssertEx.Equal(AttemptId, snapshot.AttemptId);
    }

    [Test]
    public async Task SubscribeAsync_AfterValidationEnded_RejectsTheSucceededCoderAttempt()
    {
        var service = ServiceReturning(DevelopmentTaskStatus.InReview, Attempt(AttemptId, DevelopmentAttemptStatus.Succeeded));
        await using var factory = EnabledFactory(service);
        AssertEx.True(factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>().Register(AttemptId));
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "active Development attempt");
    }

    [Test]
    public async Task SubscribeAsync_WhileValidating_RejectsACoderAttemptThatIsNotTheTasksLatest()
    {
        var service = ServiceReturning(DevelopmentTaskStatus.Validation,
            Attempt(AttemptId, DevelopmentAttemptStatus.Succeeded),
            Attempt(Guid.NewGuid(), DevelopmentAttemptStatus.Succeeded));
        await using var factory = EnabledFactory(service);
        AssertEx.True(factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>().Register(AttemptId));
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

        var exception = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId));

        AssertEx.Contains(exception.Message, "active Development attempt");
    }

    [Test]
    public async Task StopCommand_WhenItRacesTheSandboxGoingAway_Returns404()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        AssertEx.True(broker.Register(AttemptId));
        AssertEx.True(broker.TrackCommand(AttemptId, "git_status-raced", _ => throw new SandboxHandleInvalidException("The sandbox is gone.")));

        using var response = await PostStopAsync(factory, "git_status-raced");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task SubscribeAsync_WhenAttemptHasNoLiveBrokerState_RejectsSubscription()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        await using var connection = CreateConnection(factory);
        await connection.StartAsync();

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
        await connection.StartAsync();

        var snapshot = await connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId);

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
        await connection.StartAsync();
        _ = await connection.InvokeAsync<DevelopmentAttemptSubscriptionSnapshot>("SubscribeAsync", ProjectId, TaskId, AttemptId);

        var expected = Update("delivered");
        var publisher = factory.Services.GetRequiredService<IDevelopmentAttemptLiveEventPublisher>();
        await publisher.PublishAsync(expected);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal("delivered", actual.OutputDelta);
    }

    [Test]
    public async Task StopCommand_WhenTheCommandIsTrackedForTheAttempt_Returns202AndStopsIt()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        AssertEx.True(broker.Register(AttemptId));
        var stopped = 0;
        AssertEx.True(broker.TrackCommand(AttemptId, "git_status-1", _ =>
        {
            stopped++;
            return Task.CompletedTask;
        }));

        using var response = await PostStopAsync(factory, "git_status-1");

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Equal(expected: 1, stopped);
    }

    [Test]
    public async Task StopCommand_WhenTheExecutionIdIsUnknownOrBelongsToAnotherAttempt_Returns404WithoutStopping()
    {
        var service = ServiceReturning(Attempt(AttemptId, DevelopmentAttemptStatus.Running));
        await using var factory = EnabledFactory(service);
        var broker = factory.Services.GetRequiredService<IDevelopmentAttemptLiveBroker>();
        var otherAttemptId = Guid.NewGuid();
        AssertEx.True(broker.Register(AttemptId));
        AssertEx.True(broker.Register(otherAttemptId));
        var stopped = 0;
        AssertEx.True(broker.TrackCommand(otherAttemptId, "git_status-foreign", _ =>
        {
            stopped++;
            return Task.CompletedTask;
        }));

        using var foreign = await PostStopAsync(factory, "git_status-foreign");
        using var unknown = await PostStopAsync(factory, "git_status-unknown");

        AssertEx.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertEx.Equal(expected: 0, stopped);
    }

    private static async Task<HttpResponseMessage> PostStopAsync(TestServerWebAppFactory factory, string executionId)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/local/v1/development/projects/{ProjectId}/tasks/{TaskId}/attempts/{AttemptId}/commands/{executionId}/stop")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static IDevelopmentManagementService ServiceReturning(DevelopmentTaskStatus taskStatus, params DevelopmentAttemptSnapshot[] attempts)
    {
        var aggregate = DevelopmentEndpointTests.TaskAggregate(ProjectId, TaskId, attempts);
        var service = Substitute.For<IDevelopmentManagementService>();
        service.GetTaskAsync(ProjectId, TaskId, Arg.Any<CancellationToken>())
               .Returns(aggregate with
               {
                   Task = aggregate.Task with
                   {
                       Status = taskStatus
                   }
               });
        return service;
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
            .WithNodeJsonProtocol()
            .Build();

    private static DevelopmentAttemptSnapshot Attempt(Guid attemptId, DevelopmentAttemptStatus status) =>
        new()
        {
            Id = attemptId,
            TaskId = TaskId,
            PredecessorAttemptId = null,
            Role = DevelopmentAttemptRole.Coder,
            ModelId = "coder-model",
            Provider = "local",
            Status = status,
            StartedAtUtc = 1,
            EndedAtUtc = status is DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running ? null : 2,
            TerminalReason = null,
            InputTokens = null,
            OutputTokens = null,
            Version = 1
        };

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
