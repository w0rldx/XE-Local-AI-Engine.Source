namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The rank-cohort key is what decides whether two judgings are comparable, so it is fail-closed by construction:
///     an execution this node cannot fully describe gets no key at all. These cases are built from production-shaped
///     receipts and environment captures rather than hand-written identity records, because the completeness rule is
///     about what the real capture path does and does not guarantee.
/// </summary>
public sealed class BenchmarkJudgeExecutionIdentityTests
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public void Key_ForACompleteGpuLaunch_IsStableAcrossCaptureTimeOnly()
    {
        var receipt = Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33);

        var first = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash, receipt, Environment(capturedAtUtc: 1));
        var second = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash, receipt, Environment(capturedAtUtc: 999_999));

        AssertEx.NotNullOrEmpty(first);
        AssertEx.Equal(first!, second, "Two judgings on an unchanged node must share a cohort; the capture clock is not identity.");
    }

    [Test]
    public void Key_IsBoundToThePolicy()
    {
        var receipt = Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33);

        var underOnePolicy = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash, receipt, Environment());
        var underAnother = BenchmarkJudgeExecutionKey.TryCompute(new string('f', count: 64), receipt, Environment());

        AssertEx.NotEqual(underOnePolicy, underAnother, "Two policies must never share a cohort, however identical the machine.");
    }

    [Test]
    public void Key_ChangesWithTheFactsThatChangeAMeasurement()
    {
        var baseline = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
            Environment());

        AssertEx.NotEqual(baseline,
            BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Partial, offloaded: 20, total: 33),
                Environment()),
            "A different placement is a different measurement.");
        AssertEx.NotEqual(baseline,
            BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33, executableSha: new string('9', count: 64)),
                Environment()),
            "A different executable is a different runtime.");
        AssertEx.NotEqual(baseline,
            BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33, contextTokens: 8192),
                Environment()),
            "A different launch vector is a different execution.");
        AssertEx.NotEqual(baseline,
            BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
                Environment(bundleIdentity: "other-bundle")),
            "A different runtime bundle is a different runtime.");
    }

    [Test]
    public void Key_ForACpuVariantLaunch_IsComputedWithoutPlacementCountsAndSeparatesMetal()
    {
        // A CPU-variant spawn runs without a placement sniffer, so counts are legitimately absent — but the backend
        // token still separates a Linux CPU build from macOS, where llama.cpp may or may not have used Metal.
        var linux = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cpu, LlamaServerPlacementOutcome.Cpu, offloaded: null, total: null),
            Environment(gpus: []));
        var macOs = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cpu, LlamaServerPlacementOutcome.Cpu, offloaded: null, total: null, os: "macos"),
            Environment(gpus: []));

        AssertEx.NotNullOrEmpty(linux);
        AssertEx.NotNullOrEmpty(macOs);
        AssertEx.NotEqual(linux, macOs, "cpu and metal-unverified are different backends and never share a cohort.");
    }

    [Test]
    public void Key_ForACpuFallbackLaunch_StillRequiresItsCountsAndGpuIdentity()
    {
        // A GPU build that placed nothing is a GPU-variant launch: it must carry 0/N and the GPU it failed to use.
        var complete = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.None, offloaded: 0, total: 33),
            Environment());
        var withoutCounts = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.None, offloaded: null, total: null),
            Environment());
        var withoutGpu = BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
            Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.None, offloaded: 0, total: 33),
            Environment(gpus: []));

        AssertEx.NotNullOrEmpty(complete);
        AssertEx.Null(withoutCounts, "A GPU-variant launch without placement counts cannot be compared.");
        AssertEx.Null(withoutGpu, "A GPU-variant launch without a GPU identity cannot be compared.");
    }

    [Test]
    public void Key_IsNullWhenTheExecutionCannotBeFullyDescribed()
    {
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash, receipt: null, Environment()),
            "A launch that never reached readiness has nothing to key on.");
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
                environment: null),
            "Without the environment capture the runtime bundle is unknown.");
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Unknown, offloaded: null, total: null),
                Environment()),
            "An unknown backend means where the work ran was never measured.");
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33, omitExecutableSha: true),
                Environment()),
            "An unread executable digest cannot identify the binary that ran.");
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
                Environment(bundleIdentity: null)),
            "A runtime bundle that could not be captured leaves the runtime undescribed.");
        AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
                Environment(includeHardware: false)),
            "Without the host facts the os/arch of the execution is unknown.");
    }

    [Test]
    public void Key_ForAnAuxiliaryAssetLaunch_IsNull()
    {
        // The receipt records only THAT something extra was loaded, never which file, so a LoRA judging and a bare one
        // would otherwise key identically.
        foreach (var aux in new[]
                 {
                     new LlamaServerLaunchAuxAssets(HasLora: true, HasMmproj: false, HasDraft: false),
                     new LlamaServerLaunchAuxAssets(HasLora: false, HasMmproj: true, HasDraft: false),
                     new LlamaServerLaunchAuxAssets(HasLora: false, HasMmproj: false, HasDraft: true)
                 })
        {
            AssertEx.Null(BenchmarkJudgeExecutionKey.TryCompute(PolicyHash,
                    Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33, auxAssets: aux),
                    Environment()),
                "An aux-asset launch cannot be shown to be the same execution as another.");
        }
    }

    [Test]
    public void Identity_RecordsTheOptionalHostFactsWithoutRequiringThem()
    {
        var present = AssertEx.NotNull(BenchmarkJudgeExecutionKey.TryBuild(Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
            Environment()));
        var absent = AssertEx.NotNull(BenchmarkJudgeExecutionKey.TryBuild(Receipt(GpuVariant.Cuda, LlamaServerPlacementOutcome.Full, offloaded: 33, total: 33),
            Environment(cpuModel: null)));

        AssertEx.Equal("AMD Ryzen 9 9950X3D", present.CpuModel);
        AssertEx.Null(absent.CpuModel, "A fact the node does not report is recorded as absent, never as a required field.");
        AssertEx.NotEqual(BenchmarkJudgeExecutionKey.Compute(PolicyHash, present),
            BenchmarkJudgeExecutionKey.Compute(PolicyHash, absent),
            "Presence itself is canonicalised: a node that starts reporting a CPU model forms a new cohort honestly.");
    }

    private static LlamaServerLaunchReceipt Receipt(GpuVariant variant,
        LlamaServerPlacementOutcome outcome,
        int? offloaded,
        int? total,
        string? executableSha = null,
        bool omitExecutableSha = false,
        string os = "linux",
        int contextTokens = 4096,
        LlamaServerLaunchAuxAssets auxAssets = default) =>
        new(LlamaServerLaunchReceipt.CurrentVersion,
            variant,
            os,
            "b10201",
            omitExecutableSha ? null : executableSha ?? new string('e', count: 64),
            new string('m', count: 64),
            LlamaServerLaunchProjection.From(variant,
                ResolvedLaunchArguments.Replay(contextTokens),
                plan: null,
                ModelRole.Chat,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheReuse,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1.ChatCacheRamMiB),
            auxAssets,
            new LlamaServerLaunchPlacement(outcome, offloaded, total),
            contextTokens,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    private static RuntimeEnvironmentFactsV1 Environment(long capturedAtUtc = 1_700_000_000_000,
        string? bundleIdentity = "bundle-identity",
        IReadOnlyList<BenchmarkGpuFactsV1>? gpus = null,
        string? cpuModel = "AMD Ryzen 9 9950X3D",
        bool includeHardware = true) =>
        new(1,
            bundleIdentity is null ? null : new RuntimeBundleFactsV1(bundleIdentity, 3, []),
            includeHardware
                ? new BenchmarkHardwareFactsV1("Linux 6.18.33.2-microsoft-standard-WSL2",
                    "X64",
                    cpuModel,
                    8,
                    32L * 1024 * 1024 * 1024,
                    gpus ?? [new BenchmarkGpuFactsV1("NVIDIA GeForce RTX 5090", 34_359_738_368, null)],
                    "cuda")
                : null,
            new BenchmarkLlamaRuntimeFactsV1("b10201", "cuda", "prebuilt-or-unavailable", null),
            capturedAtUtc,
            includeHardware ? [] : ["hardware"]);
}
