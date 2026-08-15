namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup self-heal that refreshes persisted Quartz <c>JOB_CLASS_NAME</c> values. It is
///     best-effort by design — a node must start even when the heal fails — so the interesting assertions are which
///     failure classes are swallowed (the ones a stale/unavailable job store actually produces) and that anything
///     outside that list still escapes.
/// </summary>
public sealed class SchedulerJobDetailReconciliationServiceTests
{
    [Test]
    public async Task StartAsync_ReconcilesEveryDurableJobOnce()
    {
        using var harness = new Harness();
        _ = harness.ManagementService.ReconcileDurableJobsAsync(Arg.Any<CancellationToken>()).Returns(3);

        await harness.CreateService().StartAsync(CancellationToken.None);

        _ = await harness.ManagementService.Received(1).ReconcileDurableJobsAsync(Arg.Any<CancellationToken>());
        AssertEx.Empty(harness.Logger.Entries);
    }

    [Test]
    [Arguments(typeof(InvalidOperationException))]
    [Arguments(typeof(IOException))]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(SchedulerException))]
    [Arguments(typeof(TypeLoadException))]
    public async Task StartAsync_WhenReconciliationFails_LogsAndStillLetsTheNodeStart(Type exceptionType)
    {
        using var harness = new Harness();
        _ = harness.ManagementService.ReconcileDurableJobsAsync(Arg.Any<CancellationToken>())
                   .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);

        await harness.CreateService().StartAsync(CancellationToken.None);

        AssertEx.True(harness.Logger.HasEntry(LogLevel.Warning, "Scheduler job-detail reconciliation failed at startup"),
            "A best-effort heal that fails must still be reported, since stale jobs will keep 500-ing until it succeeds.");
    }

    [Test]
    public async Task StartAsync_WhenCancelledDuringStartup_StopsQuietly()
    {
        using var harness = new Harness();
        _ = harness.ManagementService.ReconcileDurableJobsAsync(Arg.Any<CancellationToken>())
                   .ThrowsAsync(new OperationCanceledException());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await harness.CreateService().StartAsync(cancellation.Token);

        AssertEx.Empty(harness.Logger.Entries);
    }

    [Test]
    public async Task StartAsync_WhenReconciliationFailsUnexpectedly_Propagates()
    {
        using var harness = new Harness();
        _ = harness.ManagementService.ReconcileDurableJobsAsync(Arg.Any<CancellationToken>())
                   .ThrowsAsync(new NotSupportedException("bug"));
        var service = harness.CreateService();

        _ = await AssertEx.ThrowsAsync<NotSupportedException>(() => service.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StopAsync_IsANoOp_AndNeverFiresAJob()
    {
        using var harness = new Harness();

        await harness.CreateService().StopAsync(CancellationToken.None);

        _ = await harness.ManagementService.DidNotReceiveWithAnyArgs().ReconcileDurableJobsAsync(default);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            _ = services.AddSingleton(ManagementService);
            _provider = services.BuildServiceProvider();
        }

        public IScheduledJobManagementService ManagementService { get; } = Substitute.For<IScheduledJobManagementService>();
        public RecordingLogger<SchedulerJobDetailReconciliationService> Logger { get; } = new();

        public SchedulerJobDetailReconciliationService CreateService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>(), Logger);

        public void Dispose() =>
            _provider.Dispose();
    }
}
