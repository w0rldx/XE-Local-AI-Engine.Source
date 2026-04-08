namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.BackgroundServices;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Models;
using XE_Local_AI_Engine.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolCallCleanupServiceTests
{
    [Test]
    public async Task ExecuteAsync_CallsCleanupStaleToolCalls_Periodically()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, maxAgeMinutes: 5, cleanupIntervalSeconds: 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(runner.CleanupCallCount > 0);
    }

    [Test]
    public async Task ExecuteAsync_PassesConfiguredMaxAge()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, maxAgeMinutes: 7, cleanupIntervalSeconds: 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
        await service.StopAsync(CancellationToken.None);

        AssertEx.Equal(TimeSpan.FromMinutes(7), runner.LastCleanupMaxAge);
    }

    [Test]
    public async Task ExecuteAsync_WhenCleanupThrows_DoesNotCrash()
    {
        using var runner = new MockInvocationRunner { CleanupException = new InvalidOperationException("boom") };
        using var service = CreateService(runner, maxAgeMinutes: 5, cleanupIntervalSeconds: 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
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
    }

    private static ToolCallCleanupService CreateService(IInvocationRunner runner, int maxAgeMinutes, int cleanupIntervalSeconds)
    {
        return new ToolCallCleanupService(
            runner,
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxPendingToolCallAgeMinutes = maxAgeMinutes,
                CleanupIntervalSeconds = cleanupIntervalSeconds,
            }),
            NullLogger<ToolCallCleanupService>.Instance);
    }

    private sealed class MockInvocationRunner : IInvocationRunner, IDisposable
    {
        private readonly SemaphoreSlim _cleanupSignal = new(0);

        public void Dispose() => _cleanupSignal.Dispose();

        public int CleanupCallCount { get; private set; }

        public TimeSpan LastCleanupMaxAge { get; private set; }

        public Exception? CleanupException { get; init; }

        public Task WaitForCleanupAsync(int timeoutMs = 5000) => _cleanupSignal.WaitAsync(timeoutMs);

        public Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
            CleanupCallCount++;
            LastCleanupMaxAge = maxAge;
            _cleanupSignal.Release();
            if (CleanupException is not null)
            {
                throw CleanupException;
            }
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }
}
