namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The device-inventory probe (AUD4-03): its pure <c>--list-devices</c> parser turns each device line into a
///     structured {name, total, free}, a <c>cpu</c> variant short-circuits to a determinate empty list WITHOUT touching
///     the binary manager (no process spawned), and every real-probe failure (a non-existent binary) degrades to
///     <see cref="LlamaDeviceInventory.Unknown" /> rather than a false "no GPU". The process launch itself is not
///     exercised here — the parser is the unit; the no-spawn + degrade guards are proven via a substituted binary manager.
/// </summary>
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
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(new LlamaBinary("/nonexistent/bin/llama-server", "b9692", GpuVariant.Vulkan, IsPinnedFallback: true)));
        var probe = new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance);

        var inventory = await probe.GetDeviceInventoryAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.False(inventory.ProbeSucceeded);
        AssertEx.Equal(0, inventory.Devices.Count);
        AssertEx.Equal(GpuVariant.Vulkan, inventory.Variant);
    }
}
