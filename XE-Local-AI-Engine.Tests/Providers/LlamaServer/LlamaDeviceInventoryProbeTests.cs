namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The device-inventory probe: its pure <c>--list-devices</c> parser turns each device line into a
///     structured {name, total, free}, a <c>cpu</c> variant short-circuits to a determinate empty list WITHOUT touching
///     the binary manager (no process spawned), and every real-probe failure (a non-existent binary) degrades to
///     <see cref="LlamaDeviceInventory.Unknown" /> rather than a false "no GPU". It also never ACQUIRES a runtime: with
///     none installed it reports <see cref="LlamaDeviceInventory.RuntimeNotInstalled" /> and leaves the download to the
///     explicit paths, and that answer is never cached, so an install is seen immediately. The process launch itself is
///     not exercised here — the parser is the unit; the no-spawn + no-acquire + degrade guards are proven via a
///     substituted binary manager.
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
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance);

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
                     .Returns(Task.FromResult<LlamaBinary?>(new LlamaBinary { ServerExecutablePath = "/nonexistent/bin/llama-server", Version = "b9692", Variant = GpuVariant.Vulkan, IsPinnedFallback = true }));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance);

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
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.False(inventory.ProbeSucceeded);
        AssertEx.True(inventory.RuntimeMissing);
        AssertEx.Equal(0, inventory.Devices.Count);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
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
                         Task.FromResult<LlamaBinary?>(new LlamaBinary { ServerExecutablePath = "/nonexistent/bin/llama-server", Version = "b9692", Variant = GpuVariant.Vulkan, IsPinnedFallback = true }));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance);

        var first = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);
        AssertEx.True(first.RuntimeMissing);

        var second = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        // The runtime is now resolvable, so the answer is no longer "nothing installed" — this one failed its spawn
        // (the fake path does not exist), which is the ordinary unknown, not the missing-runtime one.
        AssertEx.False(second.RuntimeMissing);
        await binaryManager.Received(2).TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }
}
