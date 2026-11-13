namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The reuse-path liveness probe bounded by its own budget, shared by every runtime supervisor.</summary>
[Category(TestCategories.Unit)]
public sealed class ProcessLivenessProbeTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CheckResponsiveWithinAsync_ProbeAnswersInBudget_ReportsItsVerdict(bool responsive)
    {
        var verdict = await ProcessLivenessProbe.CheckResponsiveWithinAsync(_ => Task.FromResult(responsive), TimeSpan.FromMinutes(1), CancellationToken.None);

        AssertEx.Equal(responsive, verdict);
    }

    [Test]
    public async Task CheckResponsiveWithinAsync_ProbeOutlivesItsBudget_CountsAsUnresponsive()
    {
        var verdict = await ProcessLivenessProbe.CheckResponsiveWithinAsync(async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            TimeSpan.Zero,
            CancellationToken.None);

        AssertEx.False(verdict, "A hung child that never answers is unresponsive, not an error.");
    }

    [Test]
    public async Task CheckResponsiveWithinAsync_CallerCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            ProcessLivenessProbe.CheckResponsiveWithinAsync(token => Task.FromCanceled<bool>(token), TimeSpan.FromMinutes(1), cts.Token));
    }
}
