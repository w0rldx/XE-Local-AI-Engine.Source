namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
///     Default GPU-vendor probe. Shells out to lightweight, ubiquitous tools and inspects their output for vendor
///     signatures. Detection failure degrades to <see cref="DetectedGpuVendor.None" /> (CPU floor) — never throws.
/// </summary>
/// <remarks>
///     This is the <em>minimal</em> probe the runtime core owns. It deliberately does NOT measure VRAM or compute a
///     memory-fit budget — that is the <c>HardwareProfiler</c>'s job. Probe order: <c>nvidia-smi</c> (NVIDIA), then a platform
///     adapter list (<c>wmic</c>/<c>lspci</c>) for AMD/Intel.
/// </remarks>
public sealed class ProcessGpuVendorProbe : IGpuVendorProbe
{
    /// <inheritdoc />
    public async Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
    {
        // NVIDIA is unambiguous when nvidia-smi succeeds; check it first on every OS.
        if (await NvidiaPresentAsync(ct).ConfigureAwait(false))
        {
            return DetectedGpuVendor.Nvidia;
        }

        var adapterText = await ReadAdapterListAsync(ct).ConfigureAwait(false);
        if (adapterText.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || adapterText.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || adapterText.Contains("advanced micro devices", StringComparison.OrdinalIgnoreCase))
        {
            return DetectedGpuVendor.Amd;
        }

        if (adapterText.Contains("intel", StringComparison.OrdinalIgnoreCase))
        {
            return DetectedGpuVendor.Intel;
        }

        return DetectedGpuVendor.None;
    }

    private static async Task<bool> NvidiaPresentAsync(CancellationToken ct)
    {
        // nvidia-smi exiting 0 with any GPU name on stdout is sufficient evidence of an NVIDIA adapter.
        var output = await TryRunAsync("nvidia-smi", "--query-gpu=name --format=csv,noheader", ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(output);
    }

    private static async Task<string> ReadAdapterListAsync(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await TryRunAsync("wmic", "path win32_VideoController get name", ct).ConfigureAwait(false) ?? string.Empty;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await TryRunAsync("lspci", string.Empty, ct).ConfigureAwait(false) ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task<string?> TryRunAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tool missing / not on PATH / permission denied — treat as "vendor not detected", never fatal.
            return null;
        }
    }
}
