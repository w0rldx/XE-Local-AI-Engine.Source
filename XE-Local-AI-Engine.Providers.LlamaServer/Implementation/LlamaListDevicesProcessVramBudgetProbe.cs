namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Real <see cref="IProcessVramBudgetProbe" /> backed by <c>llama-server --list-devices</c>. Resolves (or reuses) the
///     hash-verified llama.cpp binary for the requested backend, runs a short-lived <c>--list-devices</c> process, and
///     parses the per-device "<c>(&lt;total&gt; MiB, &lt;free&gt; MiB free)</c>" column, returning the LARGEST reported
///     process budget across devices in bytes. Vendor-agnostic — it reads llama.cpp's own device report (CUDA / Vulkan /
///     SYCL), never <c>nvidia-smi</c>, so a single code path serves every GPU backend llama.cpp supports. On WDDM the
///     value is deliberately NOT described as global free VRAM; that separate semantic comes from
///     <see cref="HardwareProfile.AvailableVramBytes" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Degrade, never throw.</b> The process-budget figure feeds placement decisions and benchmark provenance on a
///         hot path that must keep working when no GPU is present (WSL, CPU-only, headless CI). Every
///         failure mode — a <c>cpu</c>/unknown/blank backend token (no process is even spawned), an empty device list,
///         a non-zero exit with no parseable devices, the per-probe timeout, or any unexpected exception — degrades to
///         <see langword="null" /> ("unknown") rather than throwing or reporting a misleading zero. Only genuine caller
///         cancellation is surfaced (the token is honored), per the <see cref="IProcessVramBudgetProbe" /> contract.
///     </para>
///     <para>
///         <b>Process model.</b> Unlike the supervised server, <c>--list-devices</c> is a run-to-exit probe, so the
///         shared <see cref="LlamaListDevicesProcessRunner" /> (used by both this VRAM probe and the device-inventory
///         probe) launches it with both pipes drained and a bounded wait — no Job Object / setsid containment.
///     </para>
/// </remarks>
public sealed partial class LlamaListDevicesProcessVramBudgetProbe : IProcessVramBudgetProbe
{
    // Hard cap for the short-lived --list-devices probe. A wedged GPU driver could otherwise stall the invalidation hot
    // path; on overrun the child is killed (entire tree) and the figure degrades to "unknown".
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILogger<LlamaListDevicesProcessVramBudgetProbe> _logger;

    public LlamaListDevicesProcessVramBudgetProbe(ILlamaCppBinaryManager binaryManager, ILogger<LlamaListDevicesProcessVramBudgetProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(logger);
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long?> TryGetProcessBudgetBytesAsync(string backend, CancellationToken ct)
    {
        var variant = MapBackendToVariant(backend);
        if (variant is null)
        {
            // CPU / unknown / blank token: there is no GPU device list to query — report "unknown" WITHOUT spawning a
            // process. (The binary manager is never touched on this path.)
            return null;
        }

        try
        {
            var binary = await _binaryManager.EnsureBinaryAsync(variant.Value, ct).ConfigureAwait(false);
            var output = await RunListDevicesAsync(binary, ct).ConfigureAwait(false);
            return output is null ? null : TryParseMaxFreeVramBytes(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honor genuine caller cancellation — this is not a probe failure.
            throw;
        }
        catch (Exception ex)
        {
            // Binary acquisition, process launch, or any unexpected failure must never break the invalidation hot path.
            _logger.LogDebug(ex, "Process-VRAM-budget probe failed for backend {Backend}; degrading to unknown.", backend);
            return null;
        }
    }

    /// <summary>
    ///     Parses llama.cpp <c>--list-devices</c> output and returns the LARGEST free VRAM across all reported devices in
    ///     bytes, or <see langword="null" /> when no device line carries a parseable "<c>&lt;free&gt; MiB free</c>"
    ///     column (header-only output, an empty list, or unrelated text). Pure and side-effect-free so the parse is
    ///     unit-testable without spawning a process. The max is chosen deliberately: llama.cpp offloads the model to a
    ///     single device, so the device with the most free VRAM is the one most likely able to host it.
    /// </summary>
    internal static long? TryParseMaxFreeVramBytes(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        long? maxFreeMib = null;
        foreach (Match match in FreeVramRegex().Matches(output))
        {
            // Group 2 is the FREE MiB column; group 1 (total) is ignored. A line carrying only a total (no "MiB free"
            // token) does not match at all, so it cannot be mistaken for free capacity.
            if (!long.TryParse(match.Groups["free"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var freeMib))
            {
                continue;
            }

            if (maxFreeMib is null || freeMib > maxFreeMib.Value)
            {
                maxFreeMib = freeMib;
            }
        }

        return maxFreeMib is null ? null : maxFreeMib.Value * 1024L * 1024L;
    }

    // Maps the persisted lowercase backend token to its prebuilt acceleration variant. cpu / unknown / blank → null,
    // signalling "no GPU device list to query" so the caller skips the probe entirely (no process spawn).
    private static GpuVariant? MapBackendToVariant(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return null;
        }

        var token = backend.Trim();
        if (string.Equals(token, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVariant.Cuda;
        }

        if (string.Equals(token, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVariant.Vulkan;
        }

        return null;
    }

    // Trailing "(<total> MiB, <free> MiB free)" device column. Case-insensitive and tolerant of extra spaces because the
    // exact spacing/casing varies across llama.cpp builds; the named "free" group captures the free MiB (the total is a
    // non-capturing group). A 1s match timeout bounds the parse against pathological input.
    [GeneratedRegex(@"[0-9]+\s*MiB\s*,\s*(?<free>[0-9]+)\s*MiB\s*free",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex FreeVramRegex();

    // The process launch + pipe draining + bounded kill is shared with the device-inventory probe (both run the same
    // `--list-devices` command against the same binary) via LlamaListDevicesProcessRunner. A null result (spawn failure
    // or timeout overrun) degrades to "unknown" free VRAM at the caller.
    private Task<string?> RunListDevicesAsync(LlamaBinary binary, CancellationToken ct)
    {
        return LlamaListDevicesProcessRunner.RunAsync(binary.ServerExecutablePath, ProbeTimeout, _logger, ct);
    }
}
