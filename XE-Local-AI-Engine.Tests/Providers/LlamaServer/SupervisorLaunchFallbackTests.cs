namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-05: the one-shot KV-cache-quant + flash-attention fallback. When the optimized GPU launch cannot reach
///     readiness, the supervisor retries ONCE with the safe config and records the fallback per backend — but only when
///     the safe config then succeeds (so a genuinely broken model never poisons the backend's optimized-config state).
/// </summary>
public sealed class SupervisorLaunchFallbackTests
{
    [Test]
    public async Task EnsureRunning_WhenOptimizedSpawnFailsReadiness_RetriesSafeConfig_AndRecordsFallback()
    {
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        // Readiness fails on the FIRST (optimized) spawn, succeeds on the SECOND (safe) spawn.
        var healthProbe = new FirstReadinessFailsHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: healthProbe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // Two launches: the optimized attempt (with KV quant) then the safe retry (without).
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "-ctk", "the first (optimized) spawn carries the KV-cache quant.");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"), "the safe retry drops the KV-cache quant.");
        AssertEx.False(safe.Arguments.Contains("-fa"), "the safe retry drops the forced flash attention.");

        // The fallback was persisted for this backend so future spawns skip the known-bad optimized config.
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, CancellationToken.None),
            "a successful safe retry must record the optimized-config fallback for the backend.");
    }

    [Test]
    public async Task EnsureRunning_WhenFallbackAlreadyRecorded_SkipsOptimizedConfigFromTheStart()
    {
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        fallbackStore.Disable(GpuVariant.Cuda);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // Exactly one launch (no optimized attempt to fail), and it carries no KV quant.
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("-ctk"), "a backend with a recorded fallback never emits the KV-cache quant.");
    }

    /// <summary>Health probe whose readiness wait fails once (the optimized spawn) then succeeds (the safe retry).</summary>
    private sealed class FirstReadinessFailsHealthProbe : ILlamaServerHealthProbe
    {
        private int _readinessCalls;

        public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _readinessCalls);
            return Task.FromResult(call > 1);
        }

        public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult<int?>(null);
        }
    }
}
