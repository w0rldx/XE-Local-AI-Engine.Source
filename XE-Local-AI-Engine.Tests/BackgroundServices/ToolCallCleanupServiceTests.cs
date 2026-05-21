namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolCallCleanupServiceTests
{
    [Test]
    public async Task ExecuteAsync_CallsCleanupStaleToolCalls_Periodically()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, 5, 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(runner.CleanupCallCount > 0);
    }

    [Test]
    public async Task ExecuteAsync_PassesConfiguredMaxAge()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, 7, 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
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
        using var service = CreateService(runner, 5, 1);

        await service.StartAsync(CancellationToken.None);
        await runner.WaitForCleanupAsync();
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(runner.CleanupCallCount > 0);
    }

    [Test]
    public async Task StopAsync_CancelsLoop_Gracefully()
    {
        using var runner = new MockInvocationRunner();
        using var service = CreateService(runner, 5, 1);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
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
            NullLogger<ToolCallCleanupService>.Instance);
    }

    private sealed class MockInvocationRunner : IInvocationRunner, IDisposable
    {
        private readonly SemaphoreSlim _cleanupSignal = new(0);

        public int CleanupCallCount { get; private set; }

        public TimeSpan LastCleanupMaxAge { get; private set; }

        public Exception? CleanupException { get; init; }

        public void Dispose()
        {
            _cleanupSignal.Dispose();
        }

        public int ActiveInvocationCount => 0;

        public Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

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

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public Task WaitForCleanupAsync(int timeoutMs = 5000)
        {
            return _cleanupSignal.WaitAsync(timeoutMs);
        }
    }
}
