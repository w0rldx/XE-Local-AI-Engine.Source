namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The idle clock and reuse-path liveness bookkeeping every runtime supervisor's registry entry delegates to.</summary>
[Category(TestCategories.Unit)]
public sealed class ProcessLivenessTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    [Test]
    public void MarkUsed_MovesTheIdleClock_FromTheSpawnTime()
    {
        var liveness = new ProcessLiveness(Started);
        AssertEx.Equal(Started, liveness.LastUsedUtc, "A fresh child starts idle from the moment it became ready.");

        liveness.MarkUsed(Started.AddMinutes(5));

        AssertEx.Equal(Started.AddMinutes(5), liveness.LastUsedUtc);
    }

    [Test]
    public void TryClaimLivenessProbe_SucceedsOncePerInterval_CountingFromTheSpawnTime()
    {
        var liveness = new ProcessLiveness(Started);

        AssertEx.False(liveness.TryClaimLivenessProbe(Started + Interval - TimeSpan.FromTicks(1), Interval),
            "A freshly ready child is not re-probed until a full interval has passed.");
        AssertEx.True(liveness.TryClaimLivenessProbe(Started + Interval, Interval));
        AssertEx.False(liveness.TryClaimLivenessProbe(Started + Interval, Interval), "The claim advanced the probe clock.");
        AssertEx.True(liveness.TryClaimLivenessProbe(Started + Interval + Interval, Interval));
    }

    [Test]
    public void TryClaimLivenessProbe_ConcurrentReuses_ShareOneProbe()
    {
        var liveness = new ProcessLiveness(Started);
        var wins = 0;

        Parallel.For(0, 64, _ =>
        {
            if (liveness.TryClaimLivenessProbe(Started + Interval, Interval))
            {
                Interlocked.Increment(ref wins);
            }
        });

        AssertEx.Equal(expected: 1, wins, "Exactly one concurrent caller may win the probe for an interval.");
    }

    [Test]
    public void RecordLivenessFailure_CountsConsecutiveFailures_UntilReset()
    {
        var liveness = new ProcessLiveness(Started);

        AssertEx.Equal(expected: 1, liveness.RecordLivenessFailure());
        AssertEx.Equal(expected: 2, liveness.RecordLivenessFailure());

        liveness.ResetLivenessFailures();

        AssertEx.Equal(expected: 1, liveness.RecordLivenessFailure(), "A successful probe restarts the consecutive count.");
    }
}
