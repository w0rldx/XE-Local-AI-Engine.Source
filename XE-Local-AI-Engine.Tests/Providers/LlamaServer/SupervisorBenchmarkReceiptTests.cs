namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers what a BENCHMARK spawn now records about itself. A benchmark is the one spawn shape whose entire purpose
///     is a measurement someone compares later, so it is the one spawn that has to be able to say what it launched:
///     it raises log verbosity to read its own layer placement (which every other profiling spawn either did already or
///     had no use for), and it assembles a launch receipt after readiness. Nothing here may cost a run its measurement,
///     so the receipt is non-throwing and an unreadable fact is recorded as absent.
/// </summary>
public sealed class SupervisorBenchmarkReceiptTests
{
    private const string FullOffloadLine = "0.00.408.714 I load_tensors: offloaded 25/25 layers to GPU";
    private const string PartialOffloadLine = "0.00.539.550 I load_tensors: offloaded 38/49 layers to GPU";
    private const string NoOffloadLine = "0.00.539.550 I load_tensors: offloaded 0/49 layers to GPU";

    [Test]
    public async Task Benchmark_CpuReplaySpawn_EmitsContextAndThreadArgs()
    {
        // The pre-existing bug: a CPU benchmark replay bypasses the launch policy, and a CPU build emits none of a
        // frozen GPU profile's args — so the measurement ran with neither a context window nor a thread count.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchPolicyOptions: new LlamaServerLaunchPolicyOptions
            {
                CpuThreadCount = 6,
                CpuThreadsBatchCount = 8
            });

        await RunBenchmarkAsync(supervisor, ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24));

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("4096", ValueOf(spec!.Arguments, "-c"));
        AssertEx.Equal("6", ValueOf(spec.Arguments, "-t"));
        AssertEx.Equal("8", ValueOf(spec.Arguments, "-tb"));
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "A CPU spawn must not replay GPU placement.");
        AssertEx.False(spec.Arguments.Contains("-lv"), "A CPU spawn has no layer placement to observe.");
    }

    [Test]
    public async Task Benchmark_GpuReplaySpawn_KeepsItsFrozenVectorAndAddsOnlyThePlacementProbe()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        await RunBenchmarkAsync(supervisor,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true));

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));

        // Everything after the reachability prefix is pinned as one vector: the frozen replay verbatim, the role flags,
        // and nothing beyond the placement probe this change adds. The port is allocated per spawn, so the assertion
        // starts at the first flag whose value the launch decides.
        var tail = string.Join(' ', spec!.Arguments.SkipWhile(static a => !string.Equals(a, "--parallel", StringComparison.Ordinal)));
        AssertEx.Equal("--parallel 1 --no-warmup --metrics -c 8192 --n-gpu-layers 24 -ctk q8_0 -ctv q8_0 --flash-attn on "
                       + "--jinja --cache-ram 0 -lv 4",
            tail);
        AssertEx.False(spec.Arguments.Contains("-t"), "A GPU spawn carries no CPU thread policy.");
    }

    [Test]
    public async Task Benchmark_GpuSpawn_RaisesPlacementVerbosity_WhileExploreProfilingKeepsItsOwnVerboseFlag()
    {
        var benchmarkLauncher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using (var supervisor = SupervisorFactory.Create(benchmarkLauncher,
                         variantSelector: new FakeVariantSelector(GpuVariant.Cuda)))
        {
            await RunBenchmarkAsync(supervisor, ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24));
        }

        AssertEx.True(benchmarkLauncher.Launches.TryDequeue(out var benchmarkSpec));
        AssertEx.Equal("4", ValueOf(benchmarkSpec!.Arguments, "-lv"));
        AssertEx.False(benchmarkSpec.Arguments.Contains("-v"), "The benchmark spawn must not take the explore verbosity flag.");

        var exploreLauncher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var exploreSupervisor = SupervisorFactory.Create(exploreLauncher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"])));

        await exploreSupervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);

        AssertEx.True(exploreLauncher.Launches.TryDequeue(out var exploreSpec));
        AssertEx.Contains(exploreSpec!.Arguments, "-v");
        AssertEx.False(exploreSpec.Arguments.Contains("-lv"), "Explore already runs at maximum verbosity; it must not also take -lv.");
    }

    [Test]
    public async Task Benchmark_Spawn_ExposesTheExactSuccessfulLaunchArguments()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        IReadOnlyList<string> successfulLaunchArguments = [];
        await RunBenchmarkAsync(supervisor,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24),
            context =>
            {
                successfulLaunchArguments = context.SuccessfulLaunchArguments;
            });

        AssertEx.True(launcher.Launches.TryPeek(out var spec));
        AssertEx.NotEmpty(successfulLaunchArguments);
        AssertEx.True(successfulLaunchArguments.SequenceEqual(spec!.Arguments),
            "A benchmark body must receive the exact argv of the candidate that reached readiness.");
    }

    [Test]
    public async Task Benchmark_Spawn_RecordsALaunchReceiptCarryingPlacementCountsAndNothingAddressable()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FakeHealthProbe
            {
                EffectiveContextTokens = 8192
            },
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        LlamaServerLaunchReceipt? receipt = null;
        await RunBenchmarkAsync(supervisor,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true),
            context =>
            {
                receipt = context.LaunchReceipt;
            });

        var recorded = AssertEx.NotNull(receipt);
        AssertEx.Equal(LlamaServerLaunchReceipt.CurrentVersion, recorded.ReceiptVersion);
        AssertEx.Equal(GpuVariant.Cuda, recorded.Variant);
        AssertEx.NotNullOrEmpty(recorded.Os);
        AssertEx.Equal(LlamaServerPlacementOutcome.Partial, recorded.Placement.Outcome);
        AssertEx.Equal<int?>(expected: 38, recorded.Placement.OffloadedLayers);
        AssertEx.Equal<int?>(expected: 49, recorded.Placement.TotalLayers);
        AssertEx.Equal<int?>(expected: 8192, recorded.EffectiveContextTokens);
        AssertEx.Equal("q8_0", recorded.LaunchProjection.KvCacheTypeK);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionOn, recorded.LaunchProjection.FlashAttentionMode);
        AssertEx.Equal(LlamaServerBenchmarkLaunchPolicy.DeterministicV1, recorded.BenchmarkLaunchPolicy);
        AssertEx.False(recorded.AuxAssets.HasLora);
        AssertEx.False(recorded.AuxAssets.HasMmproj);
        AssertEx.False(recorded.AuxAssets.HasDraft);

        // The receipt is persisted and displayed, so nothing addressable may reach it.
        AssertEx.True(launcher.Launches.TryPeek(out var spec));
        var serialized = JsonSerializer.Serialize(recorded);
        foreach (var forbidden in new[]
                 {
                     "/fake",
                     "llama-server",
                     "model.gguf",
                     "127.0.0.1",
                     spec!.Port.ToString(CultureInfo.InvariantCulture)
                 })
        {
            AssertEx.False(serialized.Contains(forbidden, StringComparison.Ordinal),
                $"The launch receipt leaked '{forbidden}': {serialized}");
        }
    }

    [Test]
    public async Task Benchmark_Spawn_IntendedProjectionIdentity_MatchesTheRecordedOne()
    {
        // What a caller can compute BEFORE the spawn and what the spawn recorded must be the same identity, or the
        // whole intended-versus-effective comparison is meaningless.
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true);
        var intended = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
                                                      resolved,
                                                      plan: null,
                                                      ModelRole.Chat,
                                                      LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheReuse,
                                                      LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheRamMiB)
                                                  .ComputeIdentity();

        var launcher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        LlamaServerLaunchReceipt? receipt = null;
        await RunBenchmarkAsync(supervisor, resolved, context => receipt = context.LaunchReceipt);

        var recorded = AssertEx.NotNull(receipt);
        AssertEx.Equal(intended, recorded.LaunchProjection.ComputeIdentity());
        AssertEx.Empty(recorded.OmittedOptions, "Nothing was omitted, so there is nothing to explain a difference with.");
    }

    [Test]
    public async Task Benchmark_Spawn_WhenTheGateOmitsAnOption_RecordsTheEmittedShapeAndNamesTheOmission()
    {
        // The gate drops optional options the selected runtime does not advertise. On the benchmark path those are
        // --metrics (a benchmark spawns with ensureMetrics false, so nothing protects it) and -lv; an unsupported
        // KV-cache/flash-attention option refuses the launch instead. A receipt that recomputed its projection from
        // (variant, resolved, plan, role, tuning) would still claim the --metrics this process never received.
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true);
        var intended = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
                                                      resolved,
                                                      plan: null,
                                                      ModelRole.Chat,
                                                      LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheReuse,
                                                      LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheRamMiB)
                                                  .ComputeIdentity();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            capabilityManifestProbe: new FakeLlamaServerCapabilityManifestProbe(ManifestWithoutMetricsOrVerbosity()));

        LlamaServerLaunchReceipt? receipt = null;
        await RunBenchmarkAsync(supervisor, resolved, context => receipt = context.LaunchReceipt);

        var recorded = AssertEx.NotNull(receipt);
        AssertEx.True(launcher.Launches.TryPeek(out var spec));
        AssertEx.False(spec!.Arguments.Contains("--metrics"), "The gate must have removed the unsupported option.");
        AssertEx.False(recorded.LaunchProjection.Metrics, "The receipt must describe the argv, not the intent.");
        AssertEx.Contains(recorded.OmittedOptions, "--metrics");
        AssertEx.Contains(recorded.OmittedOptions, "-lv");
        AssertEx.NotEqual(intended, recorded.LaunchProjection.ComputeIdentity());

        // Everything the gate did not touch is still recorded exactly as launched.
        AssertEx.Equal("q8_0", recorded.LaunchProjection.KvCacheTypeK);
        AssertEx.Equal<int?>(expected: 8192, recorded.LaunchProjection.ContextTokens);
    }

    [Test]
    public async Task Benchmark_Spawn_WhenNoLayerLandedOnTheGpu_RecordsNoneWithBothCounts()
    {
        // 0/N is its own outcome: a GPU build serving entirely from system RAM says something different about a
        // measurement than one that placed most of its layers.
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [NoOffloadLine]
        };
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            loadTelemetry: telemetry);

        LlamaServerLaunchReceipt? receipt = null;
        await RunBenchmarkAsync(supervisor,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24),
            context => receipt = context.LaunchReceipt);

        var recorded = AssertEx.NotNull(receipt);
        AssertEx.Equal(LlamaServerPlacementOutcome.None, recorded.Placement.Outcome);
        AssertEx.Equal<int?>(expected: 0, recorded.Placement.OffloadedLayers);
        AssertEx.Equal<int?>(expected: 49, recorded.Placement.TotalLayers);

        AssertEx.True(telemetry.Observations.TryDequeue(out var observation));
        AssertEx.Equal(LlamaServerPlacementOutcome.None, observation!.Placement);
    }

    [Test]
    public async Task Benchmark_Spawn_WithNoBanner_RecordsUnknownPlacementWithoutCounts()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["0.00.100.000 I nothing about placement here"]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        LlamaServerLaunchReceipt? receipt = null;
        await RunBenchmarkAsync(supervisor,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24),
            context => receipt = context.LaunchReceipt);

        var recorded = AssertEx.NotNull(receipt);
        AssertEx.Equal(LlamaServerPlacementOutcome.Unknown, recorded.Placement.Outcome);
        AssertEx.Null(recorded.Placement.OffloadedLayers);
        AssertEx.Null(recorded.Placement.TotalLayers);
    }

    [Test]
    public async Task Profiling_NonBenchmarkSpawn_RecordsNoReceipt()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        LlamaServerLaunchReceipt? receipt = null;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24),
            enableMetrics: true,
            (context, _) =>
            {
                receipt = context.LaunchReceipt;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Null(receipt, "Only a benchmark spawn records what it launched.");
    }

    [Test]
    public void RunningImageSha256_ForThisLiveProcess_IsLowercaseHex()
    {
        // The digest is read back from the RUNNING image, so this test process is a genuine subject for it.
        var sha256 = LlamaServerProcessSupervisor.TryComputeRunningImageSha256(Environment.ProcessId);

        var digest = AssertEx.NotNull(sha256);
        AssertEx.Equal(expected: 64, digest.Length);
        AssertEx.True(digest.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"Expected lowercase hex, got '{digest}'.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MaxValue)]
    public void RunningImageSha256_WhenTheImageCannotBeRead_IsNullRatherThanAThrow(int processId)
    {
        AssertEx.Null(LlamaServerProcessSupervisor.TryComputeRunningImageSha256(processId));
    }

    [Test]
    public void BuildBenchmarkLaunchReceipt_WithEveryUnreadableFactMissing_StillProducesAReceipt()
    {
        // A receipt must never be the reason a healthy measurement fails, so every fact it cannot read has to degrade
        // to null instead of throwing — including the running-image digest for a process that does not exist.
        var receipt = LlamaServerProcessSupervisor.BuildBenchmarkLaunchReceipt(GpuVariant.Cpu,
            executableVersion: null,
            manifestSha256: null,
            LlamaServerLaunchProjection.From(GpuVariant.Cpu, ResolvedLaunchArguments.Explore(), plan: null),
            new LlamaServerLaunchAuxAssets(HasLora: false, HasMmproj: false, HasDraft: false),
            new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Cpu, OffloadedLayers: null, TotalLayers: null),
            effectiveContextTokens: null,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            processId: int.MaxValue);

        AssertEx.Null(receipt.ExecutableSha256);
        AssertEx.Null(receipt.ExecutableVersion);
        AssertEx.Null(receipt.ManifestSha256);
        AssertEx.Equal(LlamaServerLaunchReceipt.CurrentVersion, receipt.ReceiptVersion);
        AssertEx.NotNullOrEmpty(receipt.Os);
    }

    /// <summary>
    ///     A manifest for a runtime that advertises everything a benchmark spawn needs EXCEPT the two options the gate
    ///     is allowed to drop on this path. Everything the gate treats as mandatory stays listed, so the launch is
    ///     admitted rather than refused.
    /// </summary>
    private static LlamaServerCapabilityManifest ManifestWithoutMetricsOrVerbosity()
    {
        const string Help = """
                            -m, --model FNAME
                            --host HOST
                            --port PORT
                            -c, --ctx-size N
                            -ngl, --n-gpu-layers N
                            --parallel N
                            --no-warmup
                            --jinja
                            --cache-ram N
                            -fa, --flash-attn [on|off|auto]
                            -ctk, --cache-type-k TYPE
                                allowed values: f32, f16, q8_0, q4_0
                            -ctv, --cache-type-v TYPE
                                allowed values: f32, f16, q8_0, q4_0
                            """;
        return LlamaServerCapabilityManifest.FromSuccessfulProbe(new LlamaBinary("/fake/bin/llama-server", "b9692", GpuVariant.Cuda, IsPinnedFallback: true),
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            new string('a', 64),
            "version: 9692 (b9692)",
            Help);
    }

    private static Task RunBenchmarkAsync(LlamaServerProcessSupervisor supervisor,
        ResolvedLaunchArguments launchArgs,
        Action<LlamaServerProfilingContext>? inspect = null)
    {
        return supervisor.RunExclusiveBenchmarkAsync("llama3",
            ModelRole.Chat,
            launchArgs,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            (context, _) =>
            {
                inspect?.Invoke(context);
                return Task.FromResult(result: true);
            },
            CancellationToken.None);
    }

    private static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        throw new AssertionException($"Expected flag '{flag}' with a value in argument vector.");
    }
}
