namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaDeviceInventoryProbe" />. Resolves the hash-verified llama.cpp binary for the requested
///     variant, runs a short-lived <c>--list-devices</c> probe (via <see cref="LlamaListDevicesProcessRunner" />), and
///     parses each device line's <c>&lt;name&gt; (&lt;total&gt; MiB, &lt;free&gt; MiB free)</c> column into a structured
///     inventory. The answer is a pure function of the resolved binary, so a SUCCESSFUL probe is cached per
///     (variant, binary path, binary mtime) — it only changes when the binary changes (an operator installing a CUDA
///     build, or a bring-your-own override). A CPU variant short-circuits to a determinate empty list without spawning.
/// </summary>
/// <remarks>
///     Degrade, never throw: a spawn failure or the per-probe timeout yields <see cref="LlamaDeviceInventory.Unknown" />,
///     which the audit treats as "don't know" (never a false CPU-fallback alarm). A failed probe is NOT cached, so a
///     transient glitch self-heals on the next demand; only a determinate result is memoized.
/// </remarks>
public sealed partial class LlamaDeviceInventoryProbe : ILlamaDeviceInventoryProbe
{
    private const long BytesPerMib = 1024L * 1024L;

    // Hard cap for the short-lived --list-devices probe (mirrors the available-VRAM probe). A wedged GPU driver could
    // otherwise stall the audit; on overrun the child is killed and the result degrades to "unknown".
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ConcurrentDictionary<string, LlamaDeviceInventory> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<LlamaDeviceInventoryProbe> _logger;

    public LlamaDeviceInventoryProbe(ILlamaCppBinaryManager binaryManager, ILogger<LlamaDeviceInventoryProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(logger);
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlamaDeviceInventory> GetDeviceInventoryAsync(GpuVariant variant, CancellationToken ct)
    {
        if (variant == GpuVariant.Cpu)
        {
            // A CPU build has no GPU device list to enumerate — a determinate empty inventory (NOT a failed probe), and
            // no process is spawned. Whether that is a "fallback" is the audit's call, not the probe's.
            return LlamaDeviceInventory.Empty(GpuVariant.Cpu);
        }

        try
        {
            var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
            var cacheKey = BuildCacheKey(variant, binary.ServerExecutablePath);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var output = await LlamaListDevicesProcessRunner.RunAsync(binary.ServerExecutablePath, ProbeTimeout, _logger, ct).ConfigureAwait(false);
            if (output is null)
            {
                // Spawn failure / timeout: do NOT cache — a transient glitch must self-heal on the next demand.
                return LlamaDeviceInventory.Unknown(variant);
            }

            var inventory = new LlamaDeviceInventory
            {
                Variant = variant,
                ProbeSucceeded = true,
                Devices = ParseDevices(output)
            };
            _cache[cacheKey] = inventory;
            return inventory;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honor genuine caller cancellation — this is not a probe failure.
            throw;
        }
        catch (LlamaRuntimeException ex)
        {
            // The binary manager REFUSED to hand over a binary — most reachably, a bring-your-own
            // XE_LLAMACPP_SERVER_PATH override rejected by the no-silent-CPU invariant because it enumerates no GPU
            // device. That is a deliberate, operator-actionable decision, not a transient glitch, and it is the whole
            // explanation for the "backend undetermined" state the operator then sees on the hardware card. At Debug it
            // was invisible at the shipped log level, so the card's advice to check the log led nowhere — measured on
            // Windows 11 2026-08-03. Warn, so the real reason is in the log the card points at.
            _logger.LogWarning(ex,
                "Device-inventory probe could not resolve a {Variant} llama.cpp binary; the inference backend will be reported as undetermined.",
                variant);
            return LlamaDeviceInventory.Unknown(variant);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device-inventory probe failed for variant {Variant}; treating the device list as unknown.", variant);
            return LlamaDeviceInventory.Unknown(variant);
        }
    }

    /// <summary>
    ///     Parses <c>--list-devices</c> output into one <see cref="LlamaGpuDevice" /> per line carrying the
    ///     <c>(&lt;total&gt; MiB, &lt;free&gt; MiB free)</c> memory column — the device signature every GPU-backend build
    ///     prints. Pure and side-effect-free so it is unit-testable without spawning a process. Header/banner lines
    ///     (no memory column) do not match and are ignored, so an empty result means "the binary enumerated no GPU".
    /// </summary>
    internal static IReadOnlyList<LlamaGpuDevice> ParseDevices(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return
        [
            .. DeviceRegex().Matches(output).Select(static match => new LlamaGpuDevice(NormalizeName(match.Groups["name"].Value),
                ParseMibToBytes(match.Groups["total"].Value),
                ParseMibToBytes(match.Groups["free"].Value)))
        ];
    }

    // Trims the leading name text (indentation + a trailing device-id colon), defaulting to "GPU" when a build prints
    // only the memory column.
    private static string NormalizeName(string raw)
    {
        var name = raw.Trim().Trim(':').Trim();
        return name.Length == 0 ? "GPU" : name;
    }

    private static long? ParseMibToBytes(string mib)
    {
        return long.TryParse(mib, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value * BytesPerMib : null;
    }

    // Cache identity: the inventory only changes when the resolved binary changes, so key on (variant, path, mtime).
    private static string BuildCacheKey(GpuVariant variant, string executablePath)
    {
        long mtimeTicks = 0;
        try
        {
            var info = new FileInfo(executablePath);
            if (info.Exists)
            {
                mtimeTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable mtime just weakens the cache key (still keyed by variant+path); never fails the probe.
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)variant}|{executablePath}|{mtimeTicks}");
    }

    // A device line: some leading name text (no newline, up to the '('), then the "(<total> MiB, <free> MiB free)"
    // memory column. Case-insensitive and space-tolerant because spacing/casing varies across builds. A 1s match
    // timeout bounds the parse against pathological input.
    [GeneratedRegex(@"(?<name>[^\r\n(]*)\(\s*(?<total>[0-9]+)\s*MiB\s*,\s*(?<free>[0-9]+)\s*MiB\s*free\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeviceRegex();
}
