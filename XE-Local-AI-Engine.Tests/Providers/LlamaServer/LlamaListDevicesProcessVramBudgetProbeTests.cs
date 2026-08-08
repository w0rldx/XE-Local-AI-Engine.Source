namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The real available-VRAM probe: its pure <c>--list-devices</c> parser returns the LARGEST free-MiB column across
///     devices in bytes (or null when nothing is parseable), and a <c>cpu</c> backend token short-circuits to "unknown"
///     without ever touching the binary manager (no process spawned). Process-level GPU behavior is not exercised here —
///     the parser is the unit; the no-spawn guard is proven via a substituted binary manager that records no call.
/// </summary>
public sealed class LlamaListDevicesProcessVramBudgetProbeTests
{
    private const long BytesPerMib = 1024L * 1024L;

    [Test]
    public void TryParse_MultiDevice_ReturnsMaxFreeInBytes()
    {
        const string output = """
                              Available devices:
                                CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB, 23500 MiB free)
                                Vulkan0: Intel(R) Arc(tm) (16000 MiB, 15200 MiB free)
                              """;

        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(output);

        // 23500 MiB (the RTX 4090, the larger free figure) wins over the 15200 MiB Arc.
        AssertEx.Equal(23500L * BytesPerMib, result);
    }

    [Test]
    public void TryParse_SingleDevice_RoundTrips()
    {
        const string output = "  CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB, 23500 MiB free)";

        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(output);

        AssertEx.Equal(23500L * BytesPerMib, result);
    }

    [Test]
    public void TryParse_HeaderOnly_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes("Available devices:");

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_EmptyString_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(string.Empty);

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_Garbage_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes("ggml_cuda_init: no CUDA-capable device is detected");

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_TotalWithoutFreeToken_NotMatched_ReturnsNull()
    {
        // A line that reports a total but no "MiB free" column must NOT be mistaken for free capacity.
        const string output = "  CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB total)";

        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(output);

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_WhitespaceAndCaseVariation_Tolerated()
    {
        // Lower-case "mib", uppercase "FREE", and compressed/expanded spacing all still match.
        const string output = "  device0 (8192mib,7000 MiB   FREE)";

        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(output);

        AssertEx.Equal(7000L * BytesPerMib, result);
    }

    [Test]
    public void TryParse_DevicesUnordered_StillReturnsGlobalMax()
    {
        const string output = """
                              Available devices:
                                GPU0 (16000 MiB, 15200 MiB free)
                                GPU1 (24210 MiB, 23500 MiB free)
                                GPU2 (8000 MiB, 1024 MiB free)
                              """;

        var result = LlamaListDevicesProcessVramBudgetProbe.TryParseMaxFreeVramBytes(output);

        AssertEx.Equal(23500L * BytesPerMib, result);
    }

    [Test]
    public async Task TryGetFreeVramBytes_CpuToken_ReturnsNull_WithoutSpawningProcess()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();

        // If the probe were to spawn a process for a CPU backend it would first call EnsureBinaryAsync — make that loud.
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns<Task<LlamaBinary>>(_ => throw new InvalidOperationException("The CPU backend must not resolve a binary or spawn a process."));
        var probe = new LlamaListDevicesProcessVramBudgetProbe(binaryManager, NullLogger<LlamaListDevicesProcessVramBudgetProbe>.Instance);

        var result = await probe.TryGetProcessBudgetBytesAsync("cpu", CancellationToken.None);

        AssertEx.Null(result);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    public async Task TryGetFreeVramBytes_BlankToken_ReturnsNull_WithoutSpawningProcess()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var probe = new LlamaListDevicesProcessVramBudgetProbe(binaryManager, NullLogger<LlamaListDevicesProcessVramBudgetProbe>.Instance);

        var result = await probe.TryGetProcessBudgetBytesAsync("   ", CancellationToken.None);

        AssertEx.Null(result);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }
}
