namespace XE_Local_AI_Engine.Tests.Inference;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceInvalidationEvaluator" /> tests: a changed active build tag marks stale; live free VRAM below
///     the frozen baseline marks stale; and when the VRAM probe reports "unknown" the verdict degrades to the build +
///     hardware axes only (so a matching build + matching hardware is NOT stale). Every probe is mocked.
/// </summary>
public sealed class InferenceInvalidationEvaluatorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string FrozenBuild = "b9692";

    [Test]
    public async Task Invalidation_OnBuildChange_MarksStale()
    {
        // Active installed tag differs from the frozen build → stale on the build axis (hardware/VRAM match otherwise).
        var evaluator = BuildEvaluator(installedTag: "b9999", hardware: NvidiaProfile(24 * Gb), probeFreeVram: null);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task Invalidation_OnLowerFreeVram_MarksStale()
    {
        // Build + hardware match; live free VRAM (4 GB) has dropped below the frozen baseline (8 GB) → stale.
        var evaluator = BuildEvaluator(installedTag: FrozenBuild, hardware: NvidiaProfile(24 * Gb), probeFreeVram: 4 * Gb);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.True(stale);
    }

    [Test]
    public async Task Invalidation_WhenProbeUnknown_DegradesToBuildAndHwOnly()
    {
        // Probe returns null (unknown): the live-VRAM check is skipped, so a matching build + matching hardware is NOT stale.
        var evaluator = BuildEvaluator(installedTag: FrozenBuild, hardware: NvidiaProfile(24 * Gb), probeFreeVram: null);

        var stale = await evaluator.IsStaleAsync(FrozenRecord(FrozenBuild, freeVramAtFreeze: 8 * Gb), CancellationToken.None);

        AssertEx.False(stale);
    }

    private static InferenceInvalidationEvaluator BuildEvaluator(string installedTag, HardwareProfile hardware, long? probeFreeVram)
    {
        var installedStore = Substitute.For<IInstalledRuntimeStore>();
        installedStore.ReadAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<InstalledRuntimeState?>(new InstalledRuntimeState(installedTag, "llama.zip", "deadbeef", GpuVariant.Cuda, DateTimeOffset.UnixEpoch)));

        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(hardware));

        var probe = Substitute.For<IProcessVramBudgetProbe>();
        probe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(probeFreeVram));

        return new InferenceInvalidationEvaluator(installedStore, hardwareProfiler, probe, NullLogger<InferenceInvalidationEvaluator>.Instance);
    }

    private static HardwareProfile NvidiaProfile(long vramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
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
            FreeVramAtFreezeBytes: freeVramAtFreeze,
            Status: InferenceProfileStatus.Frozen,
            BenchmarkSnapshotId: Guid.NewGuid(),
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }
}
