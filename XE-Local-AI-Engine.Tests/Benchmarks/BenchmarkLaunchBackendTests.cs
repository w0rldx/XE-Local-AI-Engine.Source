namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The backend token is the one place a raw receipt is turned into a word an operator reads, so every arm is
///     pinned: a GPU build that placed nothing is <c>cpu-fallback</c>, not "cpu", and a CPU build on macOS is
///     <c>metal-unverified</c> rather than a claim either way.
/// </summary>
public sealed class BenchmarkLaunchBackendTests
{
    [Test]
    [Arguments(GpuVariant.Cpu, "linux", LlamaServerPlacementOutcome.Cpu, "cpu")]
    [Arguments(GpuVariant.Cpu, "macos", LlamaServerPlacementOutcome.Cpu, "metal-unverified")]
    [Arguments(GpuVariant.Cpu, "windows", LlamaServerPlacementOutcome.Cpu, "cpu")]
    [Arguments(GpuVariant.Cuda, "linux", LlamaServerPlacementOutcome.Full, "cuda")]
    [Arguments(GpuVariant.Cuda, "linux", LlamaServerPlacementOutcome.Partial, "cuda")]
    [Arguments(GpuVariant.Vulkan, "linux", LlamaServerPlacementOutcome.Full, "vulkan")]
    [Arguments(GpuVariant.Cuda, "linux", LlamaServerPlacementOutcome.None, "cpu-fallback")]
    [Arguments(GpuVariant.Cuda, "linux", LlamaServerPlacementOutcome.Unknown, "unknown")]
    public void From_MapsEveryPlacementAndBuildCombination(GpuVariant variant,
        string operatingSystem,
        LlamaServerPlacementOutcome placement,
        string expected)
    {
        AssertEx.Equal(expected, BenchmarkLaunchBackend.From(Receipt(variant, operatingSystem, placement)));
    }

    [Test]
    public void TryBuild_EnvironmentFactsHash_IgnoresTheCaptureClockButNotTheEnvironment()
    {
        var facts = Facts("NVIDIA GeForce RTX 5090", capturedAtUtc: 1_700_000_000_000);
        var later = Facts("NVIDIA GeForce RTX 5090", capturedAtUtc: 1_700_000_999_999);
        var otherBox = Facts("NVIDIA GeForce RTX 4090", capturedAtUtc: 1_700_000_000_000);

        var first = AssertEx.NotNull(BenchmarkLaunchEvidence.TryBuild(receipt: null, facts, BenchmarkKvCacheType.SourceAuto));
        var second = AssertEx.NotNull(BenchmarkLaunchEvidence.TryBuild(receipt: null, later, BenchmarkKvCacheType.SourceAuto));
        var third = AssertEx.NotNull(BenchmarkLaunchEvidence.TryBuild(receipt: null, otherBox, BenchmarkKvCacheType.SourceAuto));

        AssertEx.Equal(first.EnvironmentFactsHash, second.EnvironmentFactsHash,
            "Two captures of an unchanged node must hash equally, or every compare reports a difference.");
        AssertEx.NotEqual(first.EnvironmentFactsHash, third.EnvironmentFactsHash);
        AssertEx.Contains(second.EnvironmentFactsJson, "1700000999999", StringComparison.Ordinal);
    }

    private static RuntimeEnvironmentFactsV1 Facts(string gpuName, long capturedAtUtc) =>
        new(1,
            RuntimeBundle: null,
            new BenchmarkHardwareFactsV1("Linux", "X64", "AMD", 32, 68_719_476_736, [new BenchmarkGpuFactsV1(gpuName, 34_359_738_368, null)], "cuda"),
            new BenchmarkLlamaRuntimeFactsV1("b10201", "cuda", "prebuilt-or-unavailable", null),
            capturedAtUtc,
            []);

    private static LlamaServerLaunchReceipt Receipt(GpuVariant variant, string operatingSystem, LlamaServerPlacementOutcome placement) =>
        new(LlamaServerLaunchReceipt.CurrentVersion,
            variant,
            operatingSystem,
            "b10201",
            "exe-sha",
            "manifest-sha",
            LlamaServerLaunchProjection.From(variant, ResolvedLaunchArguments.Replay(4096), plan: null),
            new LlamaServerLaunchAuxAssets(false, false, false),
            new LlamaServerLaunchPlacement(placement, null, null),
            4096,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
}
