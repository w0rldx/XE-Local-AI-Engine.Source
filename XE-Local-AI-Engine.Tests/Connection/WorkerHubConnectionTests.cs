namespace XE_Local_AI_Engine.Tests.Connection;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class WorkerHubConnectionTests
{
    [Test]
    public async Task ConnectAsync_WhenNotPaired_ThrowsWorkerNotPairedException()
    {
        await using var connection = CreateConnection(MockTokenStore.Unpaired());

        await AssertEx.ThrowsAsync<WorkerNotPairedException>(() => connection.ConnectAsync());
    }

    [Test]
    public async Task ConnectAsync_WhenTokenExpired_ThrowsWorkerTokenExpiredException()
    {
        await using var connection = CreateConnection(MockTokenStore.WithExpiredToken());

        await AssertEx.ThrowsAsync<WorkerTokenExpiredException>(() => connection.ConnectAsync());
    }

    [Test]
    public async Task ConnectAsync_WhenTokenCloseToExpiry_AttemptsRefreshBeforeConnecting()
    {
        var tokenStore = MockTokenStore.Paired("expiring-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var refreshService = Substitute.For<IWorkerTokenRefreshService>();
        refreshService.TryRefreshAsync(Arg.Any<CancellationToken>()).Returns(WorkerTokenRefreshOutcome.TransientFailure);
        await using var connection = CreateConnection(tokenStore, refreshService);

        await AssertEx.ThrowsAsync<WorkerTokenExpiredException>(() => connection.ConnectAsync());

        await refreshService.Received(1).TryRefreshAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task State_InitiallyDisconnected()
    {
        await using var connection = CreateConnection(MockTokenStore.Unpaired());

        AssertEx.Equal(WorkerConnectionState.Disconnected, connection.State);
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var connection = CreateConnection(MockTokenStore.Unpaired());

        await connection.DisposeAsync();
    }

    private static WorkerHubConnection CreateConnection(MockTokenStore tokenStore, IWorkerTokenRefreshService? refreshService = null)
    {
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        var sender = new MockHubMessageSender();
        var flushService = new DeadLetterFlushService(deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        return new WorkerHubConnection(tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com"
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => Substitute.For<ICapabilityReporter>()),
            flushService,
            Substitute.For<INodeKeyRegistry>(),
            NullLogger<WorkerHubConnection>.Instance,
            workerTokenRefreshService: refreshService);
    }
}
