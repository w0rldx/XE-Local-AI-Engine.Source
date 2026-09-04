namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     D14's cutover guard, stated directly. Frozen launch intent is immutable, so the guard's whole job is to decide
///     whether this build may compare against a hash it did not compute — and to say no in BOTH directions.
/// </summary>
public sealed class BenchmarkLaunchIdentitySchemeTests
{
    [Test]
    public void NoRecordedLaunchIntent_IsNotDrained()
    {
        // A row created before launch evidence existed has nothing to compare, so nothing straddles.
        BenchmarkLaunchIdentityScheme.RequireCurrent(intent: null);
    }

    [Test]
    public void CurrentIdentityScheme_ExecutesNormally()
    {
        BenchmarkLaunchIdentityScheme.RequireCurrent(Intent(LlamaServerLaunchProjection.IdentitySchemeVersion));
    }

    [Test]
    public void QueuedUnderAnOlderIdentityScheme_FailsWithTheSupersededReason()
    {
        var thrown = AssertEx.Throws<BenchmarkExecutionException>(() =>
            BenchmarkLaunchIdentityScheme.RequireCurrent(Intent(LlamaServerLaunchProjection.IdentitySchemeVersion - 1)));

        AssertEx.Contains(thrown.Message, BenchmarkLaunchIdentityScheme.SupersededReason);
    }

    [Test]
    public void ANullStoredScheme_ReadsAsSchemeOne_AndIsDrainedOnALaterBuild()
    {
        // A legacy row loads rather than failing to deserialize, and the guard treats its NULL as scheme 1 — which
        // this build (scheme 2) refuses, exactly as it refuses an explicit 1.
        var thrown = AssertEx.Throws<BenchmarkExecutionException>(() =>
            BenchmarkLaunchIdentityScheme.RequireCurrent(Intent(launchIdentityScheme: null)));

        AssertEx.Contains(thrown.Message, BenchmarkLaunchIdentityScheme.SupersededReason);
    }

    [Test]
    public void SchemeTwoRowOnASchemeOneBuild_IsDrained()
    {
        // The reverse skew: a downgrade that left newer work queued. The guard compares with != rather than "older
        // than", so work from the FUTURE is refused exactly as firmly as work from the past. The build's own constant
        // cannot be varied at runtime, so the future is expressed as a scheme this build does not compute either.
        var thrown = AssertEx.Throws<BenchmarkExecutionException>(() =>
            BenchmarkLaunchIdentityScheme.RequireCurrent(Intent(LlamaServerLaunchProjection.IdentitySchemeVersion + 1)));

        AssertEx.Contains(thrown.Message, BenchmarkLaunchIdentityScheme.SupersededReason);
    }

    private static BenchmarkRunLaunchIntent Intent(int? launchIdentityScheme) =>
        new("cuda", "q8_0", "auto", null, LlamaServerLaunchProjection.FlashAttentionOn, new string('a', 64), null, launchIdentityScheme);
}
