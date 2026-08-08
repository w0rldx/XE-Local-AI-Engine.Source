namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SpawnContext" /> tests: the per-root-invocation spawn state. A seeded root starts at Depth 0; the
///     fan-out cap admits exactly its budget of concurrent leases and rejects the next until one is released; the
///     cloud-spawn cap counts for the whole turn and never decrements; and a missing ambient context defaults safe.
/// </summary>
public sealed class SpawnContextTests
{
    [Test]
    public async Task BeginRoot_SeedsDepthZeroContext_AndRestoresOnDispose()
    {
        AssertEx.Null(SpawnContext.Current);

        using (SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3))
        {
            var current = AssertEx.NotNull(SpawnContext.Current);
            AssertEx.Equal(0, current.Depth);
        }

        // The ambient context is restored (cleared) when the root scope is disposed.
        AssertEx.Null(SpawnContext.Current);
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryEnterFanOut_AdmitsUpToCap_ThenRejects_UntilReleased()
    {
        using var _ = SpawnContext.BeginRoot(fanOutCap: 2, cloudSpawnCap: 3);
        var context = AssertEx.NotNull(SpawnContext.Current);

        var first = context.TryEnterFanOut();
        var second = context.TryEnterFanOut();
        AssertEx.NotNull(first);
        AssertEx.NotNull(second);

        // Cap reached: a third concurrent lease is refused.
        var third = context.TryEnterFanOut();
        AssertEx.Null(third);

        // Releasing one frees a slot.
        first!.Dispose();
        var afterRelease = context.TryEnterFanOut();
        AssertEx.NotNull(afterRelease);

        afterRelease!.Dispose();
        second!.Dispose();
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryConsumeCloudSpawn_CountsForWholeTurn_AndDoesNotDecrement()
    {
        using var _ = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 2);
        var context = AssertEx.NotNull(SpawnContext.Current);

        AssertEx.True(context.TryConsumeCloudSpawn());
        AssertEx.True(context.TryConsumeCloudSpawn());
        // The cap is a per-turn total, not a concurrency count, so it is not released — the third is refused.
        AssertEx.False(context.TryConsumeCloudSpawn());
        await Task.CompletedTask;
    }

    [Test]
    public async Task Current_WithoutBeginRoot_IsNull()
    {
        // No root seeded ⇒ no ambient context ⇒ a spawn would default safe (rejected) rather than overrun.
        AssertEx.Null(SpawnContext.Current);
        await Task.CompletedTask;
    }
}
