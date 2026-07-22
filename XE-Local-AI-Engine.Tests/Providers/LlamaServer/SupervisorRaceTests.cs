namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that concurrent ensure-running for the same <c>(model, role)</c> spawns exactly once (single-flight
///     gate), and a model the reaper evicted mid-tool-call is transparently re-spawned on the next ensure-running
///     (restart, not failure).
/// </summary>
public sealed class SupervisorRaceTests
{
    [Test]
    public async Task RuntimeMutationLease_WhileHeld_BlocksEnsureUntilDisposed()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);

        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await Task.Delay(50);
        AssertEx.False(ensure.IsCompleted);
        AssertEx.Equal(0, launcher.LaunchCount);

        await lease!.DisposeAsync();
        await ensure;
        AssertEx.Equal(1, launcher.LaunchCount);
    }

    [Test]
    public async Task RuntimeMutationLease_WhenProcessRunning_FailsAtomically()
    {
        await using var supervisor = SupervisorFactory.Create();
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);

        AssertEx.Null(lease);
    }

    [Test]
    public async Task EnsureRunning_ConcurrentSameKey_SpawnsExactlyOnce()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        // Fire many concurrent ensure-running calls for the same (model, role).
        var calls = Enumerable.Range(start: 0, count: 20)
                              .Select(_ => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None))
                              .ToArray();
        var endpoints = await Task.WhenAll(calls);

        AssertEx.Equal(expected: 1, launcher.LaunchCount); // single-flight: one spawn for the whole burst.
        var first = endpoints[0].BaseAddress.AbsoluteUri;
        AssertEx.True(endpoints.All(e => string.Equals(e.BaseAddress.AbsoluteUri, first, StringComparison.Ordinal)));
    }

    [Test]
    public async Task EnsureRunning_AfterEvictionMidToolCall_RespawnsInsteadOfFailing()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var first = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        // Simulate the idle-reaper evicting the model out from under an in-flight tool call.
        await supervisor.EvictAsync("model-a", ModelRole.Chat, CancellationToken.None);

        // The tool loop re-requests the same model — this must restart it, not surface a failure.
        var second = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // original + restart.
        AssertEx.NotNull(second);
        // Both endpoints are valid localhost /v1 URLs (a fresh port may be allocated on restart).
        AssertEx.True(second.BaseAddress.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal));
        AssertEx.Equal("127.0.0.1", first.BaseAddress.Host);
    }

    [Test]
    public async Task EnsureRunning_AfterProcessCrash_RespawnsOnNextRequest()
    {
        // First launch yields a handle we crash; subsequent launches yield fresh healthy handles.
        var spawnCount = 0;
#pragma warning disable CA2000 // Ownership transfers to the supervisor (via the launcher fake), which disposes it on teardown.
        var crashHandle = new FakeProcessHandle(2000);
#pragma warning restore CA2000
        var launcher = new FakeProcessLauncher(_ =>
            Interlocked.Increment(ref spawnCount) == 1 ? crashHandle : new FakeProcessHandle(4000));
        await using var supervisor = SupervisorFactory.Create(launcher);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        crashHandle.SimulateExit(); // the process dies between requests.

        var afterCrash = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // dead process detected → respawned.
        AssertEx.NotNull(afterCrash);
    }
}
