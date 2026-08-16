namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimeEnvironmentFactsProviderTests
{
    [Test]
    public async Task CaptureAsync_EveryPartAvailable_RecordsBundleHardwareAndRuntimeWithNothingMissing()
    {
        var bundle = CreateBundle();
        try
        {
            using var provider = BuildProvider(bundle);

            var facts = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.Empty(facts.Missing, "a healthy node must capture every part");
            AssertEx.Equal(RuntimeEnvironmentFactsProvider.SchemaVersion, facts.SchemaVersion);
            AssertEx.Equal(expected: 1_700_000_000_000L, facts.CapturedAtUtc);

            var runtimeBundle = AssertEx.NotNull(facts.RuntimeBundle);
            AssertEx.Equal(expected: 2, runtimeBundle.FileCount);
            AssertEx.Equal(expected: 64, runtimeBundle.Identity.Length);
            AssertEx.Contains(runtimeBundle.Files, file => string.Equals(file.Name, "libggml.so", StringComparison.Ordinal));
            AssertEx.Empty(runtimeBundle.Files.Where(file => file.Name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)),
                "the file list carries names, never paths");

            var hardware = AssertEx.NotNull(facts.Hardware);
            AssertEx.Equal(expected: 16, hardware.LogicalCores);
            AssertEx.Equal(64L * 1024 * 1024 * 1024, hardware.RamBytes);
            AssertEx.Equal("cuda", hardware.DeviceAuditBackend);
            AssertEx.Equal(expected: 1, hardware.Gpus.Count);
            AssertEx.Equal("NVIDIA GeForce RTX 5090", hardware.Gpus[0].Name);
            AssertEx.NotEmpty(hardware.Os);
            AssertEx.NotEmpty(hardware.Arch);

            var llamaRuntime = AssertEx.NotNull(facts.LlamaRuntime);
            AssertEx.Equal("b10201", llamaRuntime.Version);
            AssertEx.Equal("Cuda", llamaRuntime.Variant);
            AssertEx.Equal("prebuilt-or-unavailable", llamaRuntime.Provenance);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Test]
    public async Task CaptureAsync_HardwarePartThrows_NamesThePartAndStillReturnsTheRest()
    {
        var bundle = CreateBundle();
        try
        {
            var hardwareProfiler = Substitute.For<IHardwareProfiler>();
            hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                            .ThrowsAsync(new InvalidOperationException("probe wedged"));
            using var provider = BuildProvider(bundle, hardwareProfiler: hardwareProfiler);

            var facts = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.Contains(facts.Missing, "hardware");
            AssertEx.Equal(expected: 1, facts.Missing.Count, "only the failed part is reported missing");
            AssertEx.Null(facts.Hardware);
            AssertEx.NotNull(facts.RuntimeBundle);
            AssertEx.NotNull(facts.LlamaRuntime);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Test]
    public async Task CaptureAsync_BinaryUnavailable_LosesOnlyTheBundleAndKeepsRuntimeProvenance()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .ThrowsAsync(new InvalidOperationException("no prebuilt for this host"));
        using var provider = BuildProvider(bundlePath: null, binaryManager: binaryManager);

        var facts = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Contains(facts.Missing, "runtimeBundle");
        AssertEx.Null(facts.RuntimeBundle);
        AssertEx.Equal("b10201", AssertEx.NotNull(facts.LlamaRuntime).Version, "the installed record still describes the runtime");
        AssertEx.NotNull(facts.Hardware);
    }

    [Test]
    public async Task CaptureAsync_SourceBuiltRuntime_RecordsManagedSourceBuildProvenanceAndCommit()
    {
        var bundle = CreateBundle();
        try
        {
            using var provider = BuildProvider(bundle,
                runtime: Runtime() with
                {
                    SourceBuildPath = "/opt/llama.cpp/build",
                    SourceCommit = new string('f', 40)
                });

            var facts = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

            var llamaRuntime = AssertEx.NotNull(facts.LlamaRuntime);
            AssertEx.Equal("managed-source-build", llamaRuntime.Provenance);
            AssertEx.Equal(new string('f', 40), llamaRuntime.SourceCommit);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Test]
    public async Task CaptureAsync_HashIsStableForTheSameNodeAndDiffersWhenTheBundleChanges()
    {
        var bundle = CreateBundle();
        try
        {
            using var provider = BuildProvider(bundle);
            var first = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);
            var second = await provider.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.Equal(BenchmarkCanonicalJson.HashOf(first), BenchmarkCanonicalJson.HashOf(second));

            await File.WriteAllTextAsync(Path.Combine(bundle, "libggml.so"), "ggml-revision-2");
            using var afterUpgrade = BuildProvider(bundle);
            var upgraded = await afterUpgrade.CaptureAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.NotEqual(BenchmarkCanonicalJson.HashOf(first), BenchmarkCanonicalJson.HashOf(upgraded));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    private static RuntimeEnvironmentFactsProvider BuildProvider(string? bundlePath,
        ILlamaCppBinaryManager? binaryManager = null,
        IHardwareProfiler? hardwareProfiler = null,
        InstalledRuntimeState? runtime = null)
    {
        if (binaryManager is null)
        {
            binaryManager = Substitute.For<ILlamaCppBinaryManager>();
            binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(new LlamaBinary(Path.Combine(bundlePath!, ExecutableName), "b10201", GpuVariant.Cuda, IsPinnedFallback: false)));
        }

        var installedRuntimeStore = Substitute.For<IInstalledRuntimeStore>();
        installedRuntimeStore.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<InstalledRuntimeState?>(runtime ?? Runtime()));

        if (hardwareProfiler is null)
        {
            hardwareProfiler = Substitute.For<IHardwareProfiler>();
            hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                            .Returns(Task.FromResult(new HardwareProfile
                            {
                                TotalRamBytes = 64L * 1024 * 1024 * 1024,
                                AvailableRamBytes = 48L * 1024 * 1024 * 1024,
                                VramBytes = 32L * 1024 * 1024 * 1024,
                                AvailableVramBytes = 30L * 1024 * 1024 * 1024,
                                VramKnown = true,
                                GpuVendor = GpuVendor.Nvidia,
                                GpuAccelAvailable = true,
                                CpuCores = 16,
                                FreeDiskBytes = 500L * 1024 * 1024 * 1024
                            }));
        }

        var deviceAudit = Substitute.For<IRuntimeDeviceAudit>();
        deviceAudit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(new RuntimeDeviceAuditState
                   {
                       InferenceBackend = "cuda",
                       GpuExpected = true,
                       CpuFallback = false,
                       Devices = [new RuntimeAuditDevice("NVIDIA GeForce RTX 5090", 32L * 1024 * 1024 * 1024, 30L * 1024 * 1024 * 1024)]
                   }));

        return new RuntimeEnvironmentFactsProvider(binaryManager,
            installedRuntimeStore,
            hardwareProfiler,
            deviceAudit,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
            NullLogger<RuntimeEnvironmentFactsProvider>.Instance);
    }

    private static InstalledRuntimeState Runtime() =>
        new("b10201", "llama-cuda.zip", new string('a', 64), GpuVariant.Cuda, DateTimeOffset.UnixEpoch);

    private static string ExecutableName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    private static string CreateBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ExecutableName), "binary-revision-1");
        File.WriteAllText(Path.Combine(directory, "libggml.so"), "ggml-revision-1");
        return directory;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
