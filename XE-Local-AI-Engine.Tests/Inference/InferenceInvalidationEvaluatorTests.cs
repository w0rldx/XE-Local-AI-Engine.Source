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
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceInvalidationEvaluator" /> tests: refreshed NVIDIA global-free VRAM is authoritative, the
///     llama.cpp process budget remains diagnostic-only, and unavailable global-free data degrades to the build +
///     hardware axes.
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
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
            processBudgetVram: null,
            out _);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task Invalidation_OnLowerGlobalFreeVram_MarksStale_HighProcessBudgetCannotMaskRegression()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 4 * Gb),
            processBudgetVram: 20 * Gb,
            out var processProbe);

        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            ProcessBudgetVramAtFreezeBytes = 20 * Gb
        };
        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.True(stale);
        await processProbe.Received(1)
                          .TryGetProcessBudgetBytesAsync("cuda", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invalidation_WhenGlobalFreeUnknown_ProcessBudgetIsDiagnosticOnly()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: null),
            processBudgetVram: 4 * Gb,
            out var processProbe);

        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            ProcessBudgetVramAtFreezeBytes = 8 * Gb
        };
        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.False(stale);
        await processProbe.Received(1).TryGetProcessBudgetBytesAsync("cuda", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invalidation_WhenProbeUnknown_DegradesToBuildAndHwOnly()
    {
        // Probe returns null (unknown): the live-VRAM check is skipped, so a matching build + matching hardware is NOT stale.
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: null),
            processBudgetVram: null,
            out _);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.False(stale);
    }

    [Test]
    public async Task Invalidation_CpuProfile_IgnoresUnrelatedGpuPressure()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 2 * Gb),
            processBudgetVram: 1 * Gb,
            out var processProbe);
        var profile = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            Backend = InferenceBackends.Cpu
        };

        var stale = await evaluator.IsStaleAsync(profile, CancellationToken.None);

        AssertEx.False(stale);
        await processProbe.DidNotReceive()
                          .TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invalidation_LegacyProfileWithoutFingerprint_IsStale()
    {
        var evaluator = BuildEvaluator(installedTag: FrozenBuild,
            hardware: NvidiaProfile(24 * Gb, availableVramBytes: 8 * Gb),
            processBudgetVram: 8 * Gb,
            out _);
        var legacy = FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb) with
        {
            LaunchPolicyFingerprintVersion = null,
            LaunchPolicyFingerprint = null
        };

        var stale = await evaluator.IsStaleAsync(legacy, CancellationToken.None);

        AssertEx.True(stale);
    }

    private static InferenceInvalidationEvaluator BuildEvaluator(string installedTag,
        HardwareProfile hardware,
        long? processBudgetVram,
        out IProcessVramBudgetProbe processProbe)
    {
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<InstalledRuntimeState?>(new InstalledRuntimeState(installedTag, "llama.zip", "deadbeef", GpuVariant.Cuda, DateTimeOffset.UnixEpoch)));

        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(hardware));

        processProbe = Substitute.For<IProcessVramBudgetProbe>();
        processProbe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(processBudgetVram));

        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<string?>("/models/model.gguf"));

        var fingerprintProvider = Substitute.For<ILaunchPolicyFingerprintProvider>();
        fingerprintProvider.CaptureAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult(new LaunchPolicyFingerprint(
                               LaunchPolicyFingerprintProvider.CurrentVersion,
                               "fingerprint")));

        return new InferenceInvalidationEvaluator(installedStore,
            modelStore,
            fingerprintProvider,
            hardwareProfiler,
            processProbe,
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
