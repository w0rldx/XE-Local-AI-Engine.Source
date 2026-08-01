namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the bounded startup-diagnostics window that feeds <see cref="LlamaStartupFailureClassifier" />, and with
///     it the context down-tier retry that depends on an OOM being classified as one.
/// </summary>
/// <remarks>
///     GPU spawns now raise llama.cpp's log verbosity to read the layer-placement banner, which puts roughly 170 lines
///     of model-loader metadata in front of any failure text. Measured against a real allocation failure on this stack:
///     at the server's default verbosity the "out of memory" line was line 11 of 18, and the SAME failure at the raised
///     verbosity was line 179 of 186. A window that kept the FIRST lines would therefore have captured only metadata
///     and classified a genuine OOM as Other, silently disabling the down-tier. Failure output sits at the END of a
///     failed startup at either verbosity, which is what the window now keeps.
/// </remarks>
public sealed class SupervisorStartupCaptureWindowTests
{
    // Verbatim from a real CUDA allocation failure.
    private const string CudaOutOfMemoryLine =
        "0.27.495.305 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 2343750.00 MiB on device 0: cudaMalloc failed: out of memory";

    // The sibling line llama.cpp raises when the buffer the backend refused was the KV cache. It is an allocation
    // failure like any other; the classifier used to read it as a KV/flash-attention compatibility problem, which
    // disabled the down-tier for the exhaustion shape a memory-tight box hits most often.
    private const string KvCacheAllocationFailureLine =
        "0.27.495.410 E llama_kv_cache: failed to allocate buffer for kv cache";

    [Test]
    public async Task EnsureRunning_OomBehindALongVerbosePrologue_IsStillClassified_AndDownTiersTheContext()
    {
        // 170 lines of model-loader metadata, exactly what the raised verbosity emits before the failure.
        var prologue = Enumerable.Range(start: 1, count: 170)
                                 .Select(static index =>
                                     $"0.00.4{index:D2}.000 I llama_model_loader: - kv {index.ToString(CultureInfo.InvariantCulture)}: general.name str = qwen3")
                                 .ToList();
        prologue.Add(CudaOutOfMemoryLine);

        var resolver = new DownTierRecordingAllocationResolver();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = prologue
        };

        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new NeverReadyHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            allocationResolver: resolver);

        await AssertEx.ThrowsAsync<Exception>(async () =>
            await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None));

        AssertEx.True(resolver.DownTierAttempts > 0,
            "an out-of-memory line behind a long verbose prologue must still be classified as OOM and drive the context down-tier.");
    }

    [Test]
    public async Task EnsureRunning_KvCacheAllocationFailure_DownTiersTheContext()
    {
        var resolver = new DownTierRecordingAllocationResolver();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [CudaOutOfMemoryLine, KvCacheAllocationFailureLine]
        };

        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new NeverReadyHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            allocationResolver: resolver);

        await AssertEx.ThrowsAsync<Exception>(async () =>
            await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None));

        AssertEx.True(resolver.DownTierAttempts > 0,
            "a refused KV-cache buffer is an out-of-memory failure and must drive the context down-tier.");
    }

    [Test]
    public async Task EnsureRunning_FailureWithNoOomEvidence_DoesNotDownTier()
    {
        // The window keeping the LAST lines must not make the classifier trigger-happy: an ordinary failure with no
        // allocation evidence still classifies as Other.
        var resolver = new DownTierRecordingAllocationResolver();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["0.00.297.569 W srv  llama_server: -----------------", "0.00.322.183 I srv load_model: loading model"]
        };

        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new NeverReadyHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            allocationResolver: resolver);

        await AssertEx.ThrowsAsync<Exception>(async () =>
            await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(expected: 0, resolver.DownTierAttempts);
    }

    /// <summary>Delegates to the real resolver but counts down-tier attempts, which only happen on an OOM classification.</summary>
    private sealed class DownTierRecordingAllocationResolver : IProcessContextAllocationResolver
    {
        private readonly DefaultProcessContextAllocationResolver _inner = new(new LlamaServerLaunchPolicyOptions());

        public int DownTierAttempts { get; private set; }

        public Task<ProcessContextAllocation?> ResolveAsync(string modelName,
            ModelRole role,
            GpuVariant variant,
            ResolvedLaunchArguments resolved,
            CancellationToken ct)
        {
            return _inner.ResolveAsync(modelName, role, variant, resolved, ct);
        }

        public bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered)
        {
            DownTierAttempts++;
            return _inner.TryDownTierAfterOutOfMemory(current, out downTiered);
        }
    }

    /// <summary>Readiness never succeeds, so every launch attempt reaches the failure-classification path.</summary>
    private sealed class NeverReadyHealthProbe : ILlamaServerHealthProbe
    {
        public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult<int?>(null);
        }
    }
}
