namespace XE_Local_AI_Engine.Tests.Inference;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceInvalidationEvaluator" /> tests: refreshed NVIDIA global-free VRAM is authoritative, the
///     cold invalidation path has no llama.cpp process-budget dependency, and unavailable global-free data degrades to
///     the build + hardware axes.
/// </summary>
public sealed class InferenceInvalidationEvaluatorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string FrozenBuild = "b9692";

    [Test]
    public async Task Invalidation_OnBuildChange_MarksStale()
    {
        // Active installed tag differs from the frozen build → stale on the build axis (hardware/VRAM match otherwise).
        var evaluator = BuildEvaluator(installedTag: "b9999",
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb));

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task Invalidation_OnLowerGlobalFreeVram_MarksStale()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 4 * Gb));

        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            ProcessBudgetVramAtFreezeBytes = 20 * Gb
        };
        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task Invalidation_RefreshesHardwareAndUsesRefreshedLowerGlobalFreeVram()
    {
        var cachedHardware = NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb);
        var refreshedHardware = NvidiaProfile(24 * Gb, availableVramBytes: 4 * Gb);
        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(forceRefresh: false, Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(cachedHardware));
        hardwareProfiler.GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(refreshedHardware));
        var evaluator = BuildEvaluator(FrozenBuild, hardwareProfiler);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb),
            CancellationToken.None);

        AssertEx.True(stale);
        await hardwareProfiler.Received(1)
                              .GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>());
        await hardwareProfiler.DidNotReceive()
                              .GetProfileAsync(forceRefresh: false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invalidation_FrozenGpuColdValidation_HasNoProcessBudgetProbeDependency()
    {
        var constructorDependsOnProcessBudgetProbe = typeof(InferenceInvalidationEvaluator)
                                                     .GetConstructors()
                                                     .SelectMany(static constructor =>
                                                         constructor.GetParameters())
                                                     .Any(static parameter =>
                                                         parameter.ParameterType == typeof(IProcessVramBudgetProbe));
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb));

        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            ProcessBudgetVramAtFreezeBytes = 8 * Gb
        };
        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.False(constructorDependsOnProcessBudgetProbe);
        AssertEx.False(stale);
    }

    [Test]
    public async Task Invalidation_WhenProbeUnknown_DegradesToBuildAndHwOnly()
    {
        // Probe returns null (unknown): the live-VRAM check is skipped, so a matching build + matching hardware is NOT stale.
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: null));

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.False(stale);
    }

    [Test]
    public async Task Invalidation_CpuProfile_IgnoresUnrelatedGpuPressure()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 2 * Gb));
        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            Backend = InferenceBackends.Cpu
        };

        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.False(stale);
    }

    [Test]
    public async Task Invalidation_LegacyProfileWithoutFingerprint_IsStale()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb));
        var legacy = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            LaunchPolicyFingerprintVersion = null,
            LaunchPolicyFingerprint = null
        };

        var stale = await evaluator.IsStaleAsync(legacy, CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task KvCacheTypeChangedSinceFreeze_IsStale()
    {
        // D13, end to end through the REAL fingerprint provider: the node's selected KV-cache type is part of a frozen
        // profile's identity, so changing it drifts axis (b) and the profile must be reported stale even though the
        // build, the hardware and the free VRAM all still match. The same wiring at the unchanged type must NOT be
        // stale — otherwise this test would pass on any drift at all.
        var modelPath = Path.GetTempFileName();
        var binaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(binaryDirectory);
        var binaryPath = Path.Combine(binaryDirectory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        using var fileHashCache = new LaunchPolicyFileHashCache();
        try
        {
            await File.WriteAllTextAsync(modelPath, "revision-1");
            File.SetLastWriteTimeUtc(modelPath, DateTime.UnixEpoch);
            await File.WriteAllTextAsync(binaryPath, "binary-revision-1");

            var frozenAtQ8 = FingerprintProvider(binaryPath, fileHashCache, LlamaServerKvCacheTypes.Q8_0);
            var record = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb);
            var captured = await frozenAtQ8.CaptureAsync(record, modelPath, CancellationToken.None);
            var frozen = record with
            {
                LaunchPolicyFingerprintVersion = captured.Version,
                LaunchPolicyFingerprint = captured.Value
            };

            var unchanged = BuildEvaluator(FrozenBuild,
                NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
                modelPath,
                frozenAtQ8);
            AssertEx.False(await unchanged.IsStaleAsync(frozen, CancellationToken.None),
                "Nothing changed, so the profile must still be valid.");

            var afterKvChange = BuildEvaluator(FrozenBuild,
                NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
                modelPath,
                FingerprintProvider(binaryPath, fileHashCache, LlamaServerKvCacheTypes.Q4_0));
            AssertEx.True(await afterKvChange.IsStaleAsync(frozen, CancellationToken.None),
                "Selecting a different KV-cache type must stale a frozen profile through the launch-policy axis.");
        }
        finally
        {
            File.Delete(modelPath);
            Directory.Delete(binaryDirectory, recursive: true);
        }
    }

    private static LaunchPolicyFingerprintProvider FingerprintProvider(string binaryPath,
        LaunchPolicyFileHashCache fileHashCache,
        string kvCacheType)
    {
        var runtime = new InstalledRuntimeState(FrozenBuild, "llama.zip", "deadbeef", GpuVariant.Cuda, DateTimeOffset.UnixEpoch);
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<InstalledRuntimeState?>(runtime));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(new LlamaBinary(binaryPath, runtime.Tag, runtime.Variant, IsPinnedFallback: false)));
        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<string?>(null));

        return new LaunchPolicyFingerprintProvider(installedStore,
            binaryManager,
            modelStore,
            Substitute.For<IGgufModelRegistry>(),
            new LlamaServerSupervisorOptions(),
            new LlamaServerLaunchPolicyOptions
            {
                KvCacheType = kvCacheType,
                EnableGpuKvCacheQuantization = !string.Equals(kvCacheType, LlamaServerKvCacheTypes.F16, StringComparison.Ordinal)
            },
            fileHashCache);
    }

    private static InferenceInvalidationEvaluator BuildEvaluator(string installedTag,
        HardwareProfile hardware,
        string modelFilePath,
        ILaunchPolicyFingerprintProvider fingerprintProvider,
        IProcessContextAllocationResolver? allocationResolver = null)
    {
        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(hardware));

        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<InstalledRuntimeState?>(new InstalledRuntimeState(installedTag, "llama.zip", "deadbeef", GpuVariant.Cuda, DateTimeOffset.UnixEpoch)));

        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<string?>(modelFilePath));

        return new InferenceInvalidationEvaluator(installedStore,
            modelStore,
            fingerprintProvider,
            hardwareProfiler,
            allocationResolver ?? Substitute.For<IProcessContextAllocationResolver>(),
            NullLogger<InferenceInvalidationEvaluator>.Instance);
    }

    private static InferenceInvalidationEvaluator BuildEvaluator(string installedTag,
        HardwareProfile hardware)
    {
        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(hardware));

        return BuildEvaluator(installedTag, hardwareProfiler);
    }

    private static InferenceInvalidationEvaluator BuildEvaluator(string installedTag,
        IHardwareProfiler hardwareProfiler)
    {
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<InstalledRuntimeState?>(new InstalledRuntimeState(installedTag, "llama.zip", "deadbeef", GpuVariant.Cuda, DateTimeOffset.UnixEpoch)));

        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<string?>("/models/model.gguf"));

        var fingerprintProvider = Substitute.For<ILaunchPolicyFingerprintProvider>();
        fingerprintProvider.MatchesAsync(Arg.Any<InferenceProfileRecord>(),
                               Arg.Any<string>(),
                               Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult(true));

        return new InferenceInvalidationEvaluator(installedStore,
            modelStore,
            fingerprintProvider,
            hardwareProfiler,
            Substitute.For<IProcessContextAllocationResolver>(),
            NullLogger<InferenceInvalidationEvaluator>.Instance);
    }

    private static HardwareProfile NvidiaProfile(long vramBytes, long? availableVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    [Test]
    public async Task Placement_LegacyExpertOffloadRowWithNoOverrideTensor_ContradictsCurrentVerdict()
    {
        // The row an intermediate build could freeze: the explore decided expert offload but recorded no -ot, so its
        // replay would launch the model fully resident under a verdict that says the experts belong in system RAM.
        var evaluator = BuildEvaluatorWithPlacement(ProcessPlacementMode.ExpertOffload, out _);

        var contradicts =
            await evaluator.ContradictsCurrentPlacementAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
                {
                    IsMoe = true,
                    ExpertCount = 128
                },
                CancellationToken.None);

        AssertEx.True(contradicts);
    }

    [Test]
    public async Task Placement_ExpertOffloadRowThatCarriesItsOverrideTensor_IsNotContradicted()
    {
        // -ot IS the frozen expert placement, so a row that has one agrees with the verdict by construction — and the
        // verdict is never even priced.
        var evaluator = BuildEvaluatorWithPlacement(ProcessPlacementMode.ExpertOffload, out var allocationResolver);

        var contradicts = await evaluator.ContradictsCurrentPlacementAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
            {
                IsMoe = true,
                ExpertCount = 128,
                OverrideTensor = @"\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU"
            },
            CancellationToken.None);

        AssertEx.False(contradicts);
        await allocationResolver.DidNotReceiveWithAnyArgs()
                                .ResolveAsync(default!, default, default, default!, CancellationToken.None);
    }

    [Test]
    public async Task Placement_ResidentVerdictLeavesADenseRowUntouched()
    {
        // The byte-identical pin: a dense model that fits resident keeps replaying with no tensor override.
        var evaluator = BuildEvaluatorWithPlacement(ProcessPlacementMode.GpuResident, out _);

        var contradicts = await evaluator.ContradictsCurrentPlacementAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb),
            CancellationToken.None);

        AssertEx.False(contradicts);
    }

    [Test]
    public async Task Placement_WhenTheVerdictCannotBeDerived_TheAxisIsSkippedRatherThanReportingStale()
    {
        var allocationResolver = Substitute.For<IProcessContextAllocationResolver>();
        allocationResolver.ResolveAsync(Arg.Any<string>(),
                              Arg.Any<ModelRole>(),
                              Arg.Any<GpuVariant>(),
                              Arg.Any<ResolvedLaunchArguments>(),
                              Arg.Any<CancellationToken>())
                          .Returns<Task<ProcessContextAllocation?>>(_ => throw new InvalidOperationException("no facts"));

        var evaluator = BuildEvaluator(FrozenBuild,
            NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
            "/models/model.gguf",
            MatchingFingerprintProvider(),
            allocationResolver);

        AssertEx.False(await evaluator.ContradictsCurrentPlacementAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb),
            CancellationToken.None));
    }

    private static InferenceInvalidationEvaluator BuildEvaluatorWithPlacement(ProcessPlacementMode placement,
        out IProcessContextAllocationResolver allocationResolver)
    {
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.ResolveAsync(Arg.Any<string>(),
                    Arg.Any<ModelRole>(),
                    Arg.Any<GpuVariant>(),
                    Arg.Any<ResolvedLaunchArguments>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ProcessContextAllocation?>(new ProcessContextAllocation(ProcessContextTokens: 4096,
                    ModelTrainContextTokens: null,
                    ProcessContextAllocationSource.FrozenProfile,
                    placement,
                    new ResourceFootprint(8 * Gb, 8 * Gb),
                    ContentIdentity: "content",
                    CacheKey: "key")));
        allocationResolver = resolver;

        return BuildEvaluator(FrozenBuild,
            NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
            "/models/model.gguf",
            MatchingFingerprintProvider(),
            resolver);
    }

    private static ILaunchPolicyFingerprintProvider MatchingFingerprintProvider()
    {
        var fingerprintProvider = Substitute.For<ILaunchPolicyFingerprintProvider>();
        fingerprintProvider.MatchesAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult(true));
        return fingerprintProvider;
    }

    private static InferenceProfileRecord FrozenRecord(string build, long? freeVramAtFreeze)
    {
        return new InferenceProfileRecord(Id: Guid.NewGuid(),
            MachineKey: "machine-abc",
            ModelName: "bartowski/Model-GGUF:Q4_K_M",
            Role: (int)ModelRole.Chat,
            Backend: "cuda",
            LlamacppBuild: build,
            Quant: "Q4_K_M",
            CtxSize: 4096,
            NGpuLayers: 20,
            TensorSplit: null,
            OverrideTensor: null,
            KvTypeK: null,
            KvTypeV: null,
            FlashAttn: false,
            NParams: 7_000_000_000,
            IsMoe: false,
            ExpertCount: null,
            GlobalFreeVramAtFreezeBytes: freeVramAtFreeze,
            Status: InferenceProfileStatus.Frozen,
            BenchmarkSnapshotId: Guid.NewGuid(),
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LaunchPolicyFingerprintVersion: LaunchPolicyFingerprintProvider.CurrentVersion,
            LaunchPolicyFingerprint: "fingerprint");
    }
}
