namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the durable MCP agent-run startup gate. Unlike the other startup services in this host, this one
///     is deliberately <b>fail-fast</b>: dispatch may not begin against an unrepaired ledger, so a recovery failure has
///     to escape <c>StartAsync</c> and take the host down rather than being logged and swallowed.
/// </summary>
public sealed class McpAgentRunRecoveryServiceTests
{
    private static readonly McpAgentRunLedgerCounters Counters = new(AccountingVersion: 1,
        NonterminalRunCount: 0,
        QueuedRunCount: 0,
        RunningRunCount: 0,
        IdentityCount: 0,
        ActivePayloadBytes: 0,
        TombstoneLogicalBytes: 0,
        UpdatedAtUtc: 0);

    [Test]
    public async Task StartAsync_WhenTheLedgerIsConsistentAndNothingWasInterrupted_RepairsQuietly()
    {
        using var harness = new Harness();

        await harness.CreateService().StartAsync(CancellationToken.None);

        _ = await harness.Store.Received(1).VerifyLedgerAsync(Arg.Any<CancellationToken>());
        _ = await harness.Store.Received(1).ReconcileInterruptedRunsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        _ = await harness.Store.DidNotReceiveWithAnyArgs().RebuildLedgerAsync(default, default);
        AssertEx.Empty(harness.Logger.Entries);
    }

    [Test]
    public async Task StartAsync_WhenPriorClaimsWereInterrupted_TerminalizesThemAndWarns()
    {
        using var harness = new Harness();
        _ = harness.Store.ReconcileInterruptedRunsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(4);

        await harness.CreateService().StartAsync(CancellationToken.None);

        AssertEx.True(harness.Logger.HasEntry(LogLevel.Warning, "Terminalized 4 non-replayable durable MCP agent run claim(s)"),
            "A non-replayable claim that had to be terminalized is operator-visible state, not a silent repair.");
    }

    [Test]
    public async Task StartAsync_StampsTheReconcileCompletionFromTheInjectedClock()
    {
        using var harness = new Harness();

        await harness.CreateService().StartAsync(CancellationToken.None);

        var expected = harness.Time.GetUtcNow().ToUnixTimeMilliseconds();
        _ = await harness.Store.Received(1).ReconcileInterruptedRunsAsync(expected, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WhenRecoveryFails_LogsCriticalAndBlocksStartup()
    {
        using var harness = new Harness();
        _ = harness.Store.ReconcileInterruptedRunsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new InvalidOperationException("node.sqlite is locked"));
        var service = harness.CreateService();

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        AssertEx.True(harness.Logger.HasEntry(LogLevel.Critical, "Durable MCP agent run recovery failed"),
            "The fail-fast path must say why the host is refusing to start.");
    }

    [Test]
    public async Task StopAsync_IsANoOp()
    {
        using var harness = new Harness();

        await harness.CreateService().StopAsync(CancellationToken.None);

        _ = await harness.Store.DidNotReceiveWithAnyArgs().VerifyLedgerAsync(default);
    }

    private sealed class Harness : IDisposable
    {
        private readonly McpAgentRunMetrics _metrics = new();
        private readonly ServiceProvider _provider;

        public Harness()
        {
            _ = Store.VerifyLedgerAsync(Arg.Any<CancellationToken>())
                     .Returns(new McpAgentRunLedgerVerification(IsConsistent: true, Counters, Counters));
            _ = Store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>())
                     .Returns(new McpAgentRunLedgerSnapshot(QueueDepth: 0, RunningCount: 0, Counters));
            _ = Store.ReconcileInterruptedRunsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(0);

            var services = new ServiceCollection();
            _ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            _ = services.AddSingleton(Store);
            _ = services.AddSingleton(_metrics);
            _ = services.AddSingleton<TimeProvider>(Time);
            _ = services.AddScoped<McpAgentRunAccountingService>();
            _provider = services.BuildServiceProvider();
        }

        public IMcpAgentRunStore Store { get; } = Substitute.For<IMcpAgentRunStore>();
        public ManualTimeProvider Time { get; } = new();
        public RecordingLogger<McpAgentRunRecoveryService> Logger { get; } = new();

        public McpAgentRunRecoveryService CreateService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>(), _metrics, Time, Logger);

        public void Dispose()
        {
            _provider.Dispose();
            _metrics.Dispose();
        }
    }
}
