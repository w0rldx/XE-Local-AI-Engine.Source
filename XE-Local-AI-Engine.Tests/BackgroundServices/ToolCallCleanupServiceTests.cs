namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
public sealed class ToolCallCleanupServiceTests
{
    [Test]
    public async Task ExecuteAsync_CallsCleanupStaleToolCalls_Periodically()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, maxAgeMinutes: 5, cleanupIntervalSeconds: 0);

        await service.StartAsync(CancellationToken.None);
        AssertEx.True(await runner.WaitForCleanupAsync());
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(runner.CleanupCallCount > 0);
    }

    [Test]
    public async Task ExecuteAsync_PassesConfiguredMaxAge()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, maxAgeMinutes: 7, cleanupIntervalSeconds: 0);

        await service.StartAsync(CancellationToken.None);
        AssertEx.True(await runner.WaitForCleanupAsync());
        await service.StopAsync(CancellationToken.None);

        AssertEx.Equal(TimeSpan.FromMinutes(7), runner.LastCleanupMaxAge);
    }

    [Test]
    public async Task ExecuteAsync_WhenCleanupThrows_DoesNotCrash()
    {
        using var runner = new MockInvocationRunner
        {
            CleanupException = new InvalidOperationException("boom")
        };
        using var service = CreateService(runner, maxAgeMinutes: 5, cleanupIntervalSeconds: 0);

        await service.StartAsync(CancellationToken.None);
        AssertEx.True(await runner.WaitForCleanupAsync());
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(runner.CleanupCallCount > 0);
    }

    [Test]
    public async Task StopAsync_CancelsLoop_Gracefully()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, maxAgeMinutes: 5, cleanupIntervalSeconds: 1);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // BackgroundService starts ExecuteAsync on the pool, so a stop can land before the loop runs (Canceled) or
        // inside its delay (RanToCompletion). Either is graceful; a fault is not.
        var loop = AssertEx.NotNull(service.ExecuteTask, "StartAsync launched the cleanup loop.");
        AssertEx.True(loop.IsCompleted && !loop.IsFaulted, $"the loop has stopped without faulting (status {loop.Status}).");
    }

    private static ToolCallCleanupService CreateService(IInvocationRunner runner, int maxAgeMinutes, int cleanupIntervalSeconds)
    {
        return new ToolCallCleanupService(runner,
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxPendingToolCallAgeMinutes = maxAgeMinutes,
                CleanupIntervalSeconds = cleanupIntervalSeconds
            }),
            StubNodeRuntimeSettings.Create()
                                   .WithMaxPendingToolCallAgeMinutes(maxAgeMinutes)
                                   .Build(),
            NullLogger<ToolCallCleanupService>.Instance);
    }

    private sealed class MockInvocationRunner : InvocationRunnerStub, IDisposable
    {
        private readonly SemaphoreSlim _cleanupSignal = new(0);

        public int CleanupCallCount { get; private set; }

        public TimeSpan LastCleanupMaxAge { get; private set; }

        public Exception? CleanupException { get; init; }

        public void Dispose()
        {
            _cleanupSignal.Dispose();
        }

        public override Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public override void CleanupStaleToolCalls(TimeSpan maxAge)
        {
            CleanupCallCount++;
            LastCleanupMaxAge = maxAge;
            _cleanupSignal.Release();
            if (CleanupException is not null)
            {
                throw CleanupException;
            }
        }

        public Task<bool> WaitForCleanupAsync(int timeoutMs = 5000)
        {
            return _cleanupSignal.WaitAsync(timeoutMs);
        }
    }
}
