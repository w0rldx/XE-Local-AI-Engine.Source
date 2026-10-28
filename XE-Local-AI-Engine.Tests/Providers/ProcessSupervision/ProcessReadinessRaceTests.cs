namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The shared readiness-versus-exit race every runtime supervisor and the transient launcher await.</summary>
[Category(TestCategories.Unit)]
public sealed class ProcessReadinessRaceTests
{
    [Test]
    [Arguments(true, ProcessReadinessOutcome.Ready)]
    [Arguments(false, ProcessReadinessOutcome.TimedOut)]
    public async Task RunAsync_LiveChild_ReportsTheProbeVerdict(bool ready, ProcessReadinessOutcome expected)
    {
        using var handle = new FakeProcessHandle(pid: 4242);

        var outcome = await ProcessReadinessRace.RunAsync(handle, _ => Task.FromResult(ready), new AdvanceableTimeProvider(), CancellationToken.None);

        AssertEx.Equal(expected, outcome);
    }

    [Test]
    public async Task RunAsync_ChildExitedBeforeReady_ReportsExitedAndCancelsTheProbe()
    {
        using var handle = new FakeProcessHandle(pid: 4242);
        handle.SimulateExit(exitCode: 1);
        var probeCancelled = false;

        var outcome = await ProcessReadinessRace.RunAsync(handle,
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    probeCancelled = true;
                    throw;
                }
            },
            new AdvanceableTimeProvider(),
            CancellationToken.None);

        AssertEx.Equal(ProcessReadinessOutcome.Exited, outcome);
        AssertEx.True(probeCancelled, "The losing readiness probe must be cancelled and awaited before the race returns.");
    }

    [Test]
    public async Task RunAsync_CallerCancelled_Propagates()
    {
        using var handle = new FakeProcessHandle(pid: 4242);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            ProcessReadinessRace.RunAsync(handle, token => Task.FromCanceled<bool>(token), new AdvanceableTimeProvider(), cts.Token));
    }
}
