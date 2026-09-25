namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The node's <see cref="ICudaDeviceProbe" />: answers "absent" when the cached llama.cpp device audit already saw the CUDA build
///     enumerate zero devices, and otherwise defers to <see cref="DefaultCudaDeviceProbe" />.
/// </summary>
/// <remarks>
///     The default probe only checks that the driver library exists, so a box whose driver enumerates no CUDA device still reads as present
///     and the whisper/sd CUDA daemon crashes on first use. The audit is only peeked, never computed: this probe runs synchronously on the
///     spawn path, and <c>--list-devices</c> is far too slow for it.
/// </remarks>
public sealed class RuntimeAuditCudaDeviceProbe : ICudaDeviceProbe
{
    private readonly IRuntimeDeviceAudit _audit;
    private readonly ICudaDeviceProbe _fallback;
    private readonly ILogger<RuntimeAuditCudaDeviceProbe> _logger;
    private int _warned;

    public RuntimeAuditCudaDeviceProbe(IRuntimeDeviceAudit audit, ILogger<RuntimeAuditCudaDeviceProbe> logger)
        : this(audit, new DefaultCudaDeviceProbe(), logger)
    {
    }

    internal RuntimeAuditCudaDeviceProbe(IRuntimeDeviceAudit audit, ICudaDeviceProbe fallback, ILogger<RuntimeAuditCudaDeviceProbe> logger)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool HasEnumerableCudaDevice()
    {
        if (!CudaEnumeratesNoDevice(_audit.PeekCached()))
        {
            return _fallback.HasEnumerableCudaDevice();
        }

        if (Interlocked.Exchange(ref _warned, value: 1) == 0)
        {
            _logger.LogWarning("The llama.cpp device audit found that the NVIDIA driver enumerates no CUDA device; the transcription and image "
                               + "runtimes skip their CUDA builds while the audit reports it. Repair or update the NVIDIA driver to restore GPU use.");
        }

        return false;
    }

    /// <summary>
    ///     True only when the CUDA llama.cpp build RAN and saw zero devices on a box that advertises a GPU. A CPU variant chosen on a GPU box,
    ///     or a Vulkan build without an ICD, says nothing about the CUDA driver, so neither rules CUDA out.
    /// </summary>
    internal static bool CudaEnumeratesNoDevice(RuntimeDeviceAuditState? audit)
    {
        return audit is { SelectedVariant: GpuVariant.Cuda, GpuExpected: true, CpuFallback: true, InferenceBackend: "cpu" };
    }
}
