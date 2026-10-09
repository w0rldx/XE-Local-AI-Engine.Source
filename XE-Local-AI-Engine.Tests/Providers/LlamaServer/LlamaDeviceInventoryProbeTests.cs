namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The device-inventory probe: its pure <c>--list-devices</c> parser turns each device line into a
///     structured {name, total, free}, a <c>cpu</c> variant short-circuits to a determinate empty list WITHOUT touching
///     the binary manager (no process spawned), and every real-probe failure (a non-existent binary) degrades to
///     <see cref="LlamaDeviceInventory.Unknown" /> rather than a false "no GPU". It also never ACQUIRES a runtime: with
///     none installed it reports <see cref="LlamaDeviceInventory.RuntimeNotInstalled" /> and leaves the download to the
///     explicit paths, and that answer is never cached, so an install is seen immediately. The parser is the unit; the
///     no-spawn + no-acquire + degrade guards are proven via a substituted binary manager, and the Windows-CUDA
///     half-install case via the real manager and a POSIX stub process.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaDeviceInventoryProbeTests
{
    private const long BytesPerMib = 1024L * 1024L;

    [Test]
    public void ParseDevices_MultiDevice_ReturnsNamedDevicesWithBytes()
    {
        const string output = """
                              Available devices:
                                CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB, 23500 MiB free)
                                Vulkan0: Intel(R) Arc(tm) (16000 MiB, 15200 MiB free)
                              """;

        var devices = LlamaDeviceInventoryProbe.ParseDevices(output);

        AssertEx.Equal(2, devices.Count);
        AssertEx.Equal("CUDA0: NVIDIA GeForce RTX 4090", devices[0].Name);
        AssertEx.Equal(24210L * BytesPerMib, devices[0].TotalBytes);
        AssertEx.Equal(23500L * BytesPerMib, devices[0].FreeBytes);
        AssertEx.Equal(15200L * BytesPerMib, devices[1].FreeBytes);
    }

    [Test]
    public void ParseDevices_HeaderOnly_ReturnsEmpty()
    {
        // Header/banner lines carry no memory column, so they are not devices — an empty list means "no GPU enumerated".
        AssertEx.Equal(0, LlamaDeviceInventoryProbe.ParseDevices("Available devices:").Count);
    }

    [Test]
    public void ParseDevices_Garbage_ReturnsEmpty()
    {
        AssertEx.Equal(0, LlamaDeviceInventoryProbe.ParseDevices("ggml_cuda_init: no CUDA-capable device is detected").Count);
    }

    [Test]
    public void ParseDevices_EmptyString_ReturnsEmpty()
    {
        AssertEx.Equal(0, LlamaDeviceInventoryProbe.ParseDevices(string.Empty).Count);
    }

    [Test]
    public async Task GetDeviceInventory_CpuVariant_ReturnsDeterminateEmpty_WithoutSpawningProcess()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns<Task<LlamaBinary>>(_ => throw new InvalidOperationException("The CPU variant must not resolve a binary or spawn a process."));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.True(inventory.ProbeSucceeded);
        AssertEx.Equal(0, inventory.Devices.Count);
        AssertEx.False(inventory.HasGpuDevice);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    public async Task GetDeviceInventory_GpuVariant_NonExistentBinary_DegradesToUnknown()
    {
        // The resolved binary path does not exist, so the --list-devices launch fails — the probe must report Unknown
        // (ProbeSucceeded false), NOT a determinate empty list, so the audit never mistakes a probe failure for "no GPU".
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(new LlamaBinary
                     {
                         ServerExecutablePath = "/nonexistent/bin/llama-server",
                         Version = "b9692",
                         Variant = GpuVariant.Vulkan,
                         IsPinnedFallback = true
                     }));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.False(inventory.ProbeSucceeded);
        AssertEx.False(inventory.RuntimeMissing, "a runtime that IS installed but unprobeable is a malfunction, not a missing runtime");
        AssertEx.Equal(0, inventory.Devices.Count);
        AssertEx.Equal(GpuVariant.Vulkan, inventory.Variant);
    }

    [Test]
    public async Task GetDeviceInventory_NoRuntimeInstalled_ReportsRuntimeMissing_AndNeverAcquiresOne()
    {
        // The defect this pins: a read-only diagnostic (the hardware-profile GET the app shell fires on every
        // authenticated page) downloaded a multi-hundred-megabyte llama.cpp runtime, including on a node whose operator
        // had just chosen the Offline / Manual profile. The probe must report "unknown, because nothing is installed"
        // and leave acquisition to the explicit paths. EnsureBinaryAsync is the tripwire: reaching it is the defect.
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(null));
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns<Task<LlamaBinary>>(_ => throw new InvalidOperationException("The device probe must never acquire a runtime."));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.False(inventory.ProbeSucceeded);
        AssertEx.True(inventory.RuntimeMissing);
        AssertEx.Equal(0, inventory.Devices.Count);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    public async Task GetDeviceInventory_InstalledCudaWithIncompleteCompanionSet_IsReportedAsIncomplete_NotMissing()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(null));
        binaryManager.IsCompanionSetIncompleteAsync(GpuVariant.Cuda, Arg.Any<CancellationToken>()).Returns(true);
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.False(inventory.ProbeSucceeded);
        AssertEx.False(inventory.RuntimeMissing);
        AssertEx.True(inventory.CompanionSetIncomplete);
    }

    [Test]
    public async Task GetDeviceInventory_MissingRuntime_IsNotCached_SoAnInstallIsSeenOnTheNextCall()
    {
        // The caching trap: "unknown because nothing is installed" must not outlive the install. Only a SUCCESSFUL probe
        // is memoized, so the probe must re-ask the binary manager every time it got nothing — proven here by the second
        // lookup happening at all (the second call resolves a binary, which is exactly the post-install state).
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(null),
                         Task.FromResult<LlamaBinary?>(new LlamaBinary
                         {
                             ServerExecutablePath = "/nonexistent/bin/llama-server",
                             Version = "b9692",
                             Variant = GpuVariant.Vulkan,
                             IsPinnedFallback = true
                         }));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

        var first = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
        AssertEx.True(first.RuntimeMissing);

        var second = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        // The runtime is now resolvable, so the answer is no longer "nothing installed" — this one failed its spawn
        // (the fake path does not exist), which is the ordinary unknown, not the missing-runtime one.
        AssertEx.False(second.RuntimeMissing);
        await binaryManager.Received(2).TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task GetDeviceInventory_WindowsCudaBuildWithoutCudart_SpawnsNothingAndCachesNothing_UntilCudartArrives()
    {
        // Pins the cached empty GPU list from a Windows CUDA build probed before its cudart DLLs arrived. The POSIX stub, like
        // the real build, lists the GPU only when cudart64_*.dll sits next to it, and marks every spawn.
        var cacheRoot = Path.Combine(Path.GetTempPath(), "xe-probe-cudart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));
            var serverPath = Path.Combine(cacheRoot, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cuda", pin.ServerRelativePath);
            var serverDir = Path.GetDirectoryName(serverPath)!;
            Directory.CreateDirectory(serverDir);
            await File.WriteAllTextAsync(serverPath, """
                                                     #!/bin/sh
                                                     dir=$(dirname "$0")
                                                     : > "$dir/spawned"
                                                     if ls "$dir"/cudart64_*.dll >/dev/null 2>&1; then
                                                       echo "  CUDA0: NVIDIA Test GPU (24000 MiB, 23000 MiB free)"
                                                     else
                                                       echo "Available devices:"
                                                     fi
                                                     """);
            File.SetUnixFileMode(serverPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var http = new HttpClient();
            var manager = new LlamaCppBinaryManager(http, cacheRoot, LlamaCppReleasePins.PinnedTag, OSPlatform.Windows, Architecture.X64, TimeProvider.System);
            var probe = new LlamaDeviceInventoryProbe(manager, NullLogger<LlamaDeviceInventoryProbe>.Instance, TimeProvider.System);

            var halfInstalled = await probe.GetDeviceInventoryAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.False(halfInstalled.RuntimeMissing, "A CUDA build without cudart is installed, only incomplete.");
            AssertEx.True(halfInstalled.CompanionSetIncomplete, "A CUDA build without cudart must be named as an incomplete companion set.");
            AssertEx.False(halfInstalled.ProbeSucceeded);
            AssertEx.False(File.Exists(Path.Combine(serverDir, "spawned")), "No --list-devices process may run against a half-CUDA dir.");

            await File.WriteAllTextAsync(Path.Combine(serverDir, "cublas64_12.dll"), "fake-cublas");
            await File.WriteAllTextAsync(Path.Combine(serverDir, "cublasLt64_12.dll"), "fake-cublaslt");
            await File.WriteAllTextAsync(Path.Combine(serverDir, "cudart64_12.dll"), "fake-cuda-runtime");

            var complete = await probe.GetDeviceInventoryAsync(GpuVariant.Cuda, CancellationToken.None);

            AssertEx.True(File.Exists(Path.Combine(serverDir, "spawned")), "Once cudart is present the probe inventories the build.");
            AssertEx.True(complete.ProbeSucceeded);
            AssertEx.True(complete.HasGpuDevice, "The inventory taken after cudart arrived must list the GPU, not a cached empty list.");
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task GetDeviceInventory_TimedOutProbe_SpawnsOnceForThreeCalls_AndRetriesAfterTheWindow()
    {
        // One cold chat asks the audit three times; a wedged --list-devices must cost one timeout, not three.
        var dir = Path.Combine(Path.GetTempPath(), "xe-probe-timeout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var wedged = await WedgedProbeAsync(dir, signal: null);
            var probe = wedged.Probe;
            var spawnLog = wedged.SpawnLog;

            for (var call = 0; call < 3; call++)
            {
                var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
                AssertEx.False(inventory.ProbeSucceeded);
                AssertEx.False(inventory.RuntimeMissing);
            }

            AssertEx.Equal(1, (await File.ReadAllLinesAsync(spawnLog)).Length);

            wedged.Clock.Advance(LlamaDeviceInventoryProbe.FailedProbeRetryAfter);
            await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

            AssertEx.Equal(2, (await File.ReadAllLinesAsync(spawnLog)).Length);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task GetDeviceInventory_RememberedFailure_IsForgottenOnRequestAndWhenTheBinaryChanges()
    {
        // Inside the retry window a remembered failure answers without spawning, unless the audit forces a refresh or
        // the binary-changed signal moved: both mean the binary it failed against may be gone.
        var dir = Path.Combine(Path.GetTempPath(), "xe-probe-forget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var signal = new CudaManagedBuildSignal();
            var wedged = await WedgedProbeAsync(dir, signal);
            var probe = wedged.Probe;
            var spawnLog = wedged.SpawnLog;

            await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
            await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
            AssertEx.Equal(1, (await File.ReadAllLinesAsync(spawnLog)).Length, "The second call is answered by the remembered failure.");

            probe.ForgetFailedProbes();
            await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
            AssertEx.Equal(2, (await File.ReadAllLinesAsync(spawnLog)).Length, "A forced refresh spawns again inside the window.");

            signal.NotifyBinaryChanged();
            await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
            AssertEx.Equal(3, (await File.ReadAllLinesAsync(spawnLog)).Length, "A changed binary spawns again inside the window.");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // A stub llama-server that records its spawn FIRST and then outlives the probe timeout, so every spawn times out
    // and is counted. The timeout is a few seconds, so even a loaded machine runs the record line before the kill.
    [UnsupportedOSPlatform("windows")]
    private static async Task<WedgedProbe> WedgedProbeAsync(string dir,
        IActiveSourceBuildSignal? signal)
    {
        Directory.CreateDirectory(dir);
        var serverPath = Path.Combine(dir, "llama-server");
        await File.WriteAllTextAsync(serverPath, """
                                                 #!/bin/sh
                                                 echo spawn >> "$(dirname "$0")/spawns"
                                                 exec sleep 60
                                                 """);
        File.SetUnixFileMode(serverPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(GpuVariant.Vulkan, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(new LlamaBinary
                     {
                         ServerExecutablePath = serverPath,
                         Version = "b9692",
                         Variant = GpuVariant.Vulkan,
                         IsPinnedFallback = true
                     }));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
        var probe = new LlamaDeviceInventoryProbe(binaryManager,
            NullLogger<LlamaDeviceInventoryProbe>.Instance,
            clock,
            TimeSpan.FromSeconds(3),
            signal);
        return new WedgedProbe
        {
            Probe = probe,
            Clock = clock,
            SpawnLog = Path.Combine(dir, "spawns")
        };
    }

    private sealed class WedgedProbe
    {
        public required LlamaDeviceInventoryProbe Probe { get; init; }

        public required ManualTimeProvider Clock { get; init; }

        public required string SpawnLog { get; init; }
    }
}
