namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     Default <see cref="ILlamaCppSourceBuildPrerequisiteProbe" />: reports — never installs — the toolchain an in-app
///     <c>llama-server</c> build needs.
/// </summary>
/// <remarks>
///     The shared probe runs the tool rows and the free-disk row; this runtime adds only its accelerator rows, right
///     after the OS gate. The NVIDIA-GPU row reuses <see cref="IGpuVendorProbe" />.
/// </remarks>
public sealed class LlamaCppSourceBuildPrerequisiteProbe : SourceBuildPrerequisiteProbe, ILlamaCppSourceBuildPrerequisiteProbe
{
    private readonly IGpuVendorProbe _vendorProbe;

    /// <summary>Creates the probe over the supplied vendor probe, defaulting the disk check to the shared app cache root.</summary>
    public LlamaCppSourceBuildPrerequisiteProbe(IGpuVendorProbe vendorProbe)
        : this(vendorProbe, RuntimeCacheDirectory.Resolve(), SourceBuildPolicy.RequiredFreeDiskBytes)
    {
    }

    /// <summary>Test seam: pins the cache root whose drive the free-disk item inspects, and the required-free-disk floor.</summary>
    internal LlamaCppSourceBuildPrerequisiteProbe(IGpuVendorProbe vendorProbe, string buildCacheRoot, long requiredFreeDiskBytes = SourceBuildPolicy.RequiredFreeDiskBytes)
        : base(buildCacheRoot, requiredFreeDiskBytes, "llama.cpp", requiresReadelf: false)
    {
        ArgumentNullException.ThrowIfNull(vendorProbe);
        _vendorProbe = vendorProbe;
    }

    /// <inheritdoc />
    public Task<SourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct)
    {
        return ProbeReportAsync(token => ProbeBackendToolsAsync(backend, token), ct);
    }

    private async Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeBackendToolsAsync(LlamaCppSourceBackend backend, CancellationToken ct)
    {
        return backend switch
        {
            LlamaCppSourceBackend.Cuda =>
            [
                await ProbeNvidiaGpuAsync(ct).ConfigureAwait(false),
                .. await ProbeCudaToolsAsync(ct).ConfigureAwait(false)
            ],
            LlamaCppSourceBackend.Vulkan => await ProbeVulkanToolsAsync(ct).ConfigureAwait(false),
            _ => []
        };
    }

    private async Task<SourceBuildPrerequisiteRow> ProbeNvidiaGpuAsync(CancellationToken ct)
    {
        var nvidiaPresent = await DetectNvidiaAsync(ct).ConfigureAwait(false);
        return new SourceBuildPrerequisiteRow
        {
            Key = "nvidia-gpu",
            Satisfied = nvidiaPresent,
            Detail = nvidiaPresent ? "NVIDIA GPU/driver detected." : "No NVIDIA GPU or driver detected."
        };
    }

    private async Task<bool> DetectNvidiaAsync(CancellationToken ct)
    {
        try
        {
            var vendor = await _vendorProbe.DetectVendorAsync(ct).ConfigureAwait(false);
            return vendor == DetectedGpuVendor.Nvidia;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A vendor-probe failure must not fail the whole checklist; treat as "not detected".
            return false;
        }
    }
}
