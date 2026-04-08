namespace XE_Local_AI_Engine.Tests.Connection;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Services.Capabilities;
using XE_Local_AI_Engine.Services.Connection;
using XE_Local_AI_Engine.Services.DeadLetter;
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

    private static WorkerHubConnection CreateConnection(MockTokenStore tokenStore)
    {
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        var sender = new MockHubMessageSender();
        var flushService = new DeadLetterFlushService(
            deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        return new WorkerHubConnection(
            tokenStore,
            Options.Create(new CentralPlatformOptions { BaseUrl = "https://test.example.com" }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => Substitute.For<ICapabilityReporter>()),
            flushService,
            NullLogger<WorkerHubConnection>.Instance);
    }
}
