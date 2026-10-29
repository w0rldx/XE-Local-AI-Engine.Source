namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The real available-VRAM probe: the <c>--list-devices</c> parser returns the largest free-MiB column in bytes
///     (null when nothing parses), and a <c>cpu</c> backend token answers "unknown" without touching the binary manager.
/// </summary>
/// <remarks>The parser is the unit; a substituted binary manager that records no call proves no spawn.</remarks>
[Category(TestCategories.Unit)]
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

        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(output);

        // 23500 MiB (the RTX 4090, the larger free figure) wins over the 15200 MiB Arc.
        AssertEx.Equal(23500L * BytesPerMib, result);
    }

    [Test]
    public void TryParse_SingleDevice_RoundTrips()
    {
        const string output = "  CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB, 23500 MiB free)";

        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(output);

        AssertEx.Equal(23500L * BytesPerMib, result);
    }

    [Test]
    public void TryParse_HeaderOnly_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull("Available devices:");

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_EmptyString_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(string.Empty);

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_Garbage_ReturnsNull()
    {
        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull("ggml_cuda_init: no CUDA-capable device is detected");

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_TotalWithoutFreeToken_NotMatched_ReturnsNull()
    {
        // A line that reports a total but no "MiB free" column must NOT be mistaken for free capacity.
        const string output = "  CUDA0: NVIDIA GeForce RTX 4090 (24210 MiB total)";

        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(output);

        AssertEx.Null(result);
    }

    [Test]
    public void TryParse_WhitespaceAndCaseVariation_Tolerated()
    {
        // Lower-case "mib", uppercase "FREE", and compressed/expanded spacing all still match.
        const string output = "  device0 (8192mib,7000 MiB   FREE)";

        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(output);

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

        var result = LlamaListDevicesProcessVramBudgetProbe.ParseMaxFreeVramBytesOrNull(output);

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

        var result = await probe.GetProcessBudgetBytesOrNullAsync("cpu", CancellationToken.None);

        AssertEx.Null(result);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    public async Task TryGetFreeVramBytes_BlankToken_ReturnsNull_WithoutSpawningProcess()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var probe = new LlamaListDevicesProcessVramBudgetProbe(binaryManager, NullLogger<LlamaListDevicesProcessVramBudgetProbe>.Instance);

        var result = await probe.GetProcessBudgetBytesOrNullAsync("   ", CancellationToken.None);

        AssertEx.Null(result);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }
}
