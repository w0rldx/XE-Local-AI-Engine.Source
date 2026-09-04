namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one-shot KV-cache-quant + flash-attention fallback. When the optimized GPU launch cannot reach
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
        var telemetry = new FakeLlamaServerLoadTelemetry();
        // Readiness fails on the FIRST (optimized) spawn, succeeds on the SECOND (safe) spawn.
        var healthProbe = new FirstReadinessFailsHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: healthProbe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // Two launches: the optimized attempt (with KV quant) then the safe retry (without).
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "-ctk", "the first (optimized) spawn carries the KV-cache quant.");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"), "the safe retry drops the KV-cache quant.");
        AssertEx.False(safe.Arguments.Contains("-fa"), "the safe retry drops the forced flash attention.");

        // The fallback was persisted for this backend so future spawns skip the known-bad optimized config.
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
            "a successful safe retry must record the optimized-config fallback for the backend.");

        var observations = telemetry.Observations.ToArray();
        AssertEx.Equal(expected: 2, observations.Length);
        AssertEx.Equal(LlamaServerReadinessOutcome.Failed, observations[0].Outcome);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observations[0].AttemptKind);
        AssertEx.Equal(LlamaServerReadinessOutcome.Ready, observations[1].Outcome);
        AssertEx.Equal(LlamaServerLoadAttemptKind.SafeRetry, observations[1].AttemptKind);
    }

    [Test]
    public async Task EnsureRunning_WhenFallbackAlreadyRecorded_SkipsOptimizedConfigFromTheStart()
    {
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        fallbackStore.Disable(GpuVariant.Cuda);
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // Exactly one launch (no optimized attempt to fail), and it carries no KV quant.
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("-ctk"), "a backend with a recorded fallback never emits the KV-cache quant.");
        AssertEx.True(telemetry.Observations.TryDequeue(out var observation));
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observation!.AttemptKind);
    }

    [Test]
    public async Task EnsureRunning_WhenTelemetrySinkThrows_PreservesSafeFallbackAndServingProcess()
    {
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FirstReadinessFailsHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            loadTelemetry: new ThrowingLlamaServerLoadTelemetry());

        var endpoint = await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.NotNull(endpoint);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.Equal(expected: 1, supervisor.CountRunningProcesses());
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
    }

    [Test]
    public async Task EnsureRunning_LoadDurationIncludesSpawnAndIgnoresWallClockSteps()
    {
        var time = new AdvanceableTimeProvider();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var launcher = new FakeProcessLauncher(_ =>
        {
            time.AdvanceWallClockOnly(TimeSpan.FromHours(-2));
            time.AdvanceTimestamp(TimeSpan.FromMilliseconds(875));
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            return new FakeProcessHandle(pid: 4242);
#pragma warning restore CA2000
        });
        await using var supervisor = SupervisorFactory.Create(launcher,
            timeProvider: time,
            loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(telemetry.Observations.TryDequeue(out var observation));
        AssertEx.Equal(expected: 875d, observation!.ReadinessDurationMs);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observation.AttemptKind);
    }

    [Test]
    public async Task EnsureRunning_WhenOptimizedChildExitsDuringLoad_StillRetriesSafeConfig()
    {
        var launches = 0;
        var launcher = new FakeProcessLauncher(_ =>
        {
#pragma warning disable CA2000 // Ownership transfers to the supervisor through the launcher fake.
            var handle = new FakeProcessHandle(Interlocked.Increment(ref launches));
#pragma warning restore CA2000
            if (launches == 1)
            {
                handle.SimulateExit();
            }

            return handle;
        });
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FirstLoadBlocksThenReadyHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "-ctk");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"));
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
    }

    [Test]
    public async Task EnsureRunning_WhenRuntimeLacksOptimizedSpelling_SelectsSafeCandidateWithoutLaunchingTheBadVector()
    {
        const string help = """
                            -m
                            --host
                            --port
                            --parallel
                            --no-warmup
                            -c
                            --metrics
                            --fit
                            --n-gpu-layers
                            -lv
                            --jinja
                            --cache-reuse
                            --cache-ram
                            -fa, --flash-attn [on|off|auto]
                            -ctk, --cache-type-k TYPE
                                allowed values: f16, q8_0
                            --cache-type-v TYPE
                                allowed values: f16, q8_0
                            """;
        var binary = new LlamaBinary("/fake/bin/llama-server", "b10201", GpuVariant.Cuda, IsPinnedFallback: true);
        var manifest = LlamaServerCapabilityManifest.FromSuccessfulProbe(binary,
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            executableSha256: new string('A', 64),
            version: "b10201",
            help);
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096,
                nGpuLayers: 24,
                kvTypeK: "q8_0",
                kvTypeV: "q8_0",
                flashAttn: true)),
            launchFallbackStore: fallbackStore,
            capabilityManifestProbe: new FakeLlamaServerCapabilityManifestProbe(manifest));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"));
        AssertEx.False(safe.Arguments.Contains("--flash-attn"));
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
    }

    [Test]
    public async Task CpuMoeSafeRetry_KeepsTheFlagAndDisablesOnlyKvQuant()
    {
        // The safe candidate may drop KV-cache quantization and NOTHING else: dropping --cpu-moe would launch the
        // over-subscription the capability gate refuses outright.
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FirstReadinessFailsHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            allocationResolver: ExpertOffloadAllocationResolver());

        await supervisor.EnsureRunningAsync("moe-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "--cpu-moe", "the primary of an expert-offload placement carries the flag.");
        AssertEx.Contains(optimized.Arguments, "-ctk");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.Contains(safe!.Arguments, "--cpu-moe", "the safe retry must carry --cpu-moe through untouched.");
        AssertEx.False(safe.Arguments.Contains("-ctk"), "the safe retry drops the KV-cache quant, and only that.");
    }

    [Test]
    public async Task ExpertOffloadSafeRetry_RecordsNothing()
    {
        // R1: an expert-offload spawn is the most VRAM-marginal launch on the box, so a one-shot success without KV
        // quantization proves nothing about KV. Recording it would disable the optimized config for EVERY model on
        // this backend from one model's placement or transient failure.
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            healthProbe: new FirstReadinessFailsHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            allocationResolver: ExpertOffloadAllocationResolver());

        await supervisor.EnsureRunningAsync("moe-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Empty(fallbackStore.Disabled,
            "an expert-offload safe retry is inconclusive about KV and must record nothing for the backend.");
        AssertEx.False(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None));
    }

    [Test]
    public async Task CpuMoeOnlyPlan_GetsNoSafeRetry()
    {
        // With KV quantization already recorded as unsupported, the plan carries --cpu-moe alone — and there is
        // nothing safe left to drop, so the builder produces ONE candidate. That is what keeps every safe retry a KV
        // retry, which is what makes the supervisor's attribution sound in the first place.
        var launcher = new FakeProcessLauncher();
        var fallbackStore = new FakeLaunchFallbackStore();
        fallbackStore.Disable(GpuVariant.Cuda);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchFallbackStore: fallbackStore,
            allocationResolver: ExpertOffloadAllocationResolver());

        await supervisor.EnsureRunningAsync("moe-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var only));
        AssertEx.Contains(only!.Arguments, "--cpu-moe");
        AssertEx.False(only.Arguments.Contains("-ctk"));
    }

    /// <summary>An allocation resolver that always places the experts in system RAM.</summary>
    private static IProcessContextAllocationResolver ExpertOffloadAllocationResolver()
    {
        var allocation = new ProcessContextAllocation(ProcessContextTokens: 8192,
            ModelTrainContextTokens: null,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.ExpertOffload,
            ResourceFootprint.Zero,
            ContentIdentity: "moe-model:0",
            CacheKey: "moe-cache");
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.ResolveAsync(Arg.Any<string>(),
                    Arg.Any<ModelRole>(),
                    Arg.Any<GpuVariant>(),
                    Arg.Any<ResolvedLaunchArguments>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<ProcessContextAllocation?>(allocation));
        return resolver;
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

    private sealed class FirstLoadBlocksThenReadyHealthProbe : ILlamaServerHealthProbe
    {
        private int _calls;

        public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return true;
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
