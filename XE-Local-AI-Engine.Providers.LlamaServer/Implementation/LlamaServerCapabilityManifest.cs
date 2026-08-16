namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>Parsed command-line surface reported by one resolved llama-server executable.</summary>
internal sealed partial record LlamaServerCapabilityManifest
{
    private LlamaServerCapabilityManifest(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc,
        string? executableSha256,
        string? version,
        bool probeSucceeded,
        IReadOnlySet<string> options,
        IReadOnlySet<string> speculativeModes,
        IReadOnlySet<string> cacheTypesK,
        IReadOnlySet<string> cacheTypesV,
        IReadOnlySet<string> flashAttentionModes,
        bool supportsAllOptions)
    {
        Binary = binary;
        ExecutableLengthBytes = executableLengthBytes;
        ExecutableLastWriteUtc = executableLastWriteUtc;
        ExecutableSha256 = executableSha256;
        Version = version;
        ProbeSucceeded = probeSucceeded;
        Options = options;
        SpeculativeModes = speculativeModes;
        CacheTypesK = cacheTypesK;
        CacheTypesV = cacheTypesV;
        FlashAttentionModes = flashAttentionModes;
        SupportsAllOptions = supportsAllOptions;
    }

    public LlamaBinary Binary { get; }

    public long ExecutableLengthBytes { get; }

    public DateTimeOffset ExecutableLastWriteUtc { get; }

    public string? ExecutableSha256 { get; }

    public string? Version { get; }

    public bool ProbeSucceeded { get; }

    public IReadOnlySet<string> Options { get; }

    public IReadOnlySet<string> SpeculativeModes { get; }

    public IReadOnlySet<string> CacheTypesK { get; }

    public IReadOnlySet<string> CacheTypesV { get; }

    public IReadOnlySet<string> FlashAttentionModes { get; }

    /// <summary>
    ///     Set only by <see cref="AllSupportedForTesting" />, which stands in for a binary whose whole option surface is
    ///     assumed. Internal rather than private so <see cref="LlamaServerLaunchCapabilityInspector" /> carries the same
    ///     assumption into its public answers instead of reporting "supports nothing".
    /// </summary>
    internal bool SupportsAllOptions { get; }

    public bool SupportsOption(string option)
    {
        return SupportsAllOptions || Options.Contains(option);
    }

    public bool SupportsSpeculativeMode(string mode)
    {
        return SupportsAllOptions || SpeculativeModes.Contains(mode);
    }

    public bool SupportsCacheTypeK(string cacheType)
    {
        return SupportsAllOptions || CacheTypesK.Contains(cacheType);
    }

    public bool SupportsCacheTypeV(string cacheType)
    {
        return SupportsAllOptions || CacheTypesV.Contains(cacheType);
    }

    public bool SupportsFlashAttentionMode(string mode)
    {
        return SupportsAllOptions || FlashAttentionModes.Contains(mode);
    }

    internal static LlamaServerCapabilityManifest FromSuccessfulProbe(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc,
        string executableSha256,
        string version,
        string help)
    {
        var parsed = ParseHelp(help);
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes,
            executableLastWriteUtc,
            executableSha256,
            version,
            probeSucceeded: true,
            parsed.Options,
            parsed.SpeculativeModes,
            parsed.CacheTypesK,
            parsed.CacheTypesV,
            parsed.FlashAttentionModes,
            supportsAllOptions: false);
    }

    internal static LlamaServerCapabilityManifest Failed(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc)
    {
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes,
            executableLastWriteUtc,
            executableSha256: null,
            version: null,
            probeSucceeded: false,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            supportsAllOptions: false);
    }

    /// <summary>Permissive test-only manifest used by supervisor fakes that never execute a real llama-server.</summary>
    internal static LlamaServerCapabilityManifest AllSupportedForTesting(LlamaBinary binary)
    {
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes: 0,
            DateTimeOffset.UnixEpoch,
            executableSha256: null,
            version: "test",
            probeSucceeded: true,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            supportsAllOptions: true);
    }

    internal static ParsedLlamaServerHelp ParseHelp(string help)
    {
        if (string.IsNullOrWhiteSpace(help))
        {
            return new ParsedLlamaServerHelp(FrozenSet<string>.Empty,
                FrozenSet<string>.Empty,
                FrozenSet<string>.Empty,
                FrozenSet<string>.Empty,
                FrozenSet<string>.Empty);
        }

        var options = help.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                          .Where(static line => line.Length > 0 && line[0] == '-')
                          .Select(static line => OptionDeclarationRegex().Match(line))
                          .Where(static match => match.Success)
                          .SelectMany(static match => match.Groups["option"].Captures.Select(static capture => capture.Value))
                          .ToFrozenSet(StringComparer.Ordinal);
        return new ParsedLlamaServerHelp(options,
            ParseCommaSeparatedValues(SpeculativeModesRegex().Match(help)),
            ParseCommaSeparatedValues(CacheTypesKRegex().Match(help)),
            ParseCommaSeparatedValues(CacheTypesVRegex().Match(help)),
            ParsePipeSeparatedValues(FlashAttentionModesRegex().Match(help)));
    }

    private static FrozenSet<string> ParseCommaSeparatedValues(Match match)
    {
        return match.Success
            ? match.Groups["values"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    private static FrozenSet<string> ParsePipeSeparatedValues(Match match)
    {
        return match.Success
            ? match.Groups["values"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    [GeneratedRegex(@"^(?<option>--?[A-Za-z][A-Za-z0-9_-]*)(?:\s*,\s*(?<option>--?[A-Za-z][A-Za-z0-9_-]*))*",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionDeclarationRegex();

    [GeneratedRegex(@"(?m)^\s*--spec-type\s+(?<values>[A-Za-z0-9_-]+(?:\s*,\s*[A-Za-z0-9_-]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpeculativeModesRegex();

    [GeneratedRegex(@"(?m)^\s*-ctk\s*,\s*--cache-type-k\s+TYPE[^\r\n]*\r?\n\s*allowed values:\s*(?<values>[A-Za-z0-9_]+(?:\s*,\s*[A-Za-z0-9_]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CacheTypesKRegex();

    [GeneratedRegex(@"(?m)^\s*-ctv\s*,\s*--cache-type-v\s+TYPE[^\r\n]*\r?\n\s*allowed values:\s*(?<values>[A-Za-z0-9_]+(?:\s*,\s*[A-Za-z0-9_]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CacheTypesVRegex();

    [GeneratedRegex(@"(?m)^\s*-fa\s*,\s*--flash-attn\s+\[(?<values>[A-Za-z0-9_-]+(?:\|[A-Za-z0-9_-]+)*)\]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex FlashAttentionModesRegex();
}

/// <summary>Immutable parser output used by focused capability tests.</summary>
internal sealed record ParsedLlamaServerHelp(
    IReadOnlySet<string> Options,
    IReadOnlySet<string> SpeculativeModes,
    IReadOnlySet<string> CacheTypesK,
    IReadOnlySet<string> CacheTypesV,
    IReadOnlySet<string> FlashAttentionModes);

/// <summary>Resolves and caches the actual option surface of a selected llama-server executable.</summary>
internal interface ILlamaServerCapabilityManifestProbe
{
    Task<LlamaServerCapabilityManifest> GetManifestAsync(LlamaBinary binary, CancellationToken ct);
}

/// <summary>
///     Probes <c>--version</c> and <c>--help</c> once per requested-version/path/length/mtime/SHA-256 identity. Only
///     successful probes are cached; failed probes are never added, so a transient spawn or driver failure can heal on
///     the next launch.
/// </summary>
internal sealed class LlamaServerCapabilityManifestProbe : ILlamaServerCapabilityManifestProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<CapabilityCacheKey, LlamaServerCapabilityManifest> _cache = new();
    private readonly ConcurrentDictionary<CapabilityCacheKey, SemaphoreSlim> _probeGates = new();
    private readonly ILogger<LlamaServerCapabilityManifestProbe> _logger;
    private readonly ILlamaCommandProcessRunner _runner;

    public LlamaServerCapabilityManifestProbe(ILogger<LlamaServerCapabilityManifestProbe> logger)
        : this(new LlamaCommandProcessRunner(logger), logger)
    {
    }

    internal LlamaServerCapabilityManifestProbe(ILlamaCommandProcessRunner runner,
        ILogger<LlamaServerCapabilityManifestProbe> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LlamaServerCapabilityManifest> GetManifestAsync(LlamaBinary binary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ExecutableIdentitySnapshot snapshot = new(0, DateTimeOffset.UnixEpoch);
        try
        {
            snapshot = ReadIdentitySnapshot(binary.ServerExecutablePath);
            var executableSha256 = await ComputeSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false);
            var key = new CapabilityCacheKey(binary.Variant,
                binary.Version,
                Path.GetFullPath(binary.ServerExecutablePath),
                snapshot.LengthBytes,
                snapshot.LastWriteUtc.UtcTicks,
                executableSha256);
            var gate = _probeGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A waiter may have observed the old file immediately before a replacement. Re-read identity after
                // acquiring the key gate and fail this attempt if it changed. A later caller will probe the new identity;
                // do not recurse while holding the old identity's single-flight gate.
                var currentSnapshot = ReadIdentitySnapshot(binary.ServerExecutablePath);
                var currentSha256 = await ComputeSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false);
                if (snapshot != currentSnapshot || !string.Equals(executableSha256, currentSha256, StringComparison.Ordinal))
                {
                    _logger.LogWarning("The selected llama-server runtime changed while waiting for its capability probe; the attempt was discarded.");
                    return LlamaServerCapabilityManifest.Failed(binary, currentSnapshot.LengthBytes, currentSnapshot.LastWriteUtc);
                }

                if (_cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                // The caller owns this bounded probe. Cancellation reaps its child and releases the gate; the next
                // waiter then probes independently, so one cancelled caller cannot poison the shared cache.
                var manifest = await ProbeCoreAsync(binary, snapshot, executableSha256, ct).ConfigureAwait(false);
                if (!manifest.ProbeSucceeded)
                {
                    return manifest;
                }

                var verifiedSnapshot = ReadIdentitySnapshot(binary.ServerExecutablePath);
                var verifiedSha256 = await ComputeSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false);
                if (snapshot != verifiedSnapshot || !string.Equals(executableSha256, verifiedSha256, StringComparison.Ordinal))
                {
                    _logger.LogWarning("The selected llama-server runtime changed while its capabilities were being probed; the result was discarded.");
                    return LlamaServerCapabilityManifest.Failed(binary, verifiedSnapshot.LengthBytes, verifiedSnapshot.LastWriteUtc);
                }

                return _cache.GetOrAdd(key, manifest);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The selected llama-server runtime capability identity could not be read.");
            return LlamaServerCapabilityManifest.Failed(binary, snapshot.LengthBytes, snapshot.LastWriteUtc);
        }
    }

    private async Task<LlamaServerCapabilityManifest> ProbeCoreAsync(LlamaBinary binary,
        ExecutableIdentitySnapshot snapshot,
        string executableSha256,
        CancellationToken ct)
    {
        try
        {
            var versionResult = await _runner.RunAsync(binary.ServerExecutablePath, ["--version"], ProbeTimeout, ct).ConfigureAwait(false);
            var helpResult = await _runner.RunAsync(binary.ServerExecutablePath, ["--help"], ProbeTimeout, ct).ConfigureAwait(false);
            if (versionResult is not { ExitCode: 0 }
                || helpResult is not { ExitCode: 0 }
                || string.IsNullOrWhiteSpace(helpResult.CombinedOutput))
            {
                _logger.LogWarning("The selected llama-server runtime did not expose a usable --version/--help capability manifest.");
                return LlamaServerCapabilityManifest.Failed(binary, snapshot.LengthBytes, snapshot.LastWriteUtc);
            }

            var parsed = LlamaServerCapabilityManifest.ParseHelp(helpResult.CombinedOutput);
            if (parsed.Options.Count == 0)
            {
                _logger.LogWarning("The selected llama-server runtime returned help without any recognizable command-line options.");
                return LlamaServerCapabilityManifest.Failed(binary, snapshot.LengthBytes, snapshot.LastWriteUtc);
            }

            var version = FirstNonEmptyLine(versionResult.CombinedOutput);
            return LlamaServerCapabilityManifest.FromSuccessfulProbe(binary,
                snapshot.LengthBytes,
                snapshot.LastWriteUtc,
                executableSha256,
                version,
                helpResult.CombinedOutput);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The selected llama-server runtime capability probe failed.");
            return LlamaServerCapabilityManifest.Failed(binary, snapshot.LengthBytes, snapshot.LastWriteUtc);
        }
    }

    private static ExecutableIdentitySnapshot ReadIdentitySnapshot(string executablePath)
    {
        try
        {
            var info = new FileInfo(executablePath);
            return info.Exists
                ? new ExecutableIdentitySnapshot(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
                : new ExecutableIdentitySnapshot(0, DateTimeOffset.UnixEpoch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ExecutableIdentitySnapshot(0, DateTimeOffset.UnixEpoch);
        }
    }

    private static async Task<string> ComputeSha256Async(string executablePath, CancellationToken ct)
    {
        await using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static string FirstNonEmptyLine(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .FirstOrDefault() ?? "unknown";
    }

    private sealed record ExecutableIdentitySnapshot(long LengthBytes, DateTimeOffset LastWriteUtc);

    private sealed record CapabilityCacheKey(
        GpuVariant Variant,
        string RequestedVersion,
        string ExecutablePath,
        long LengthBytes,
        long LastWriteUtcTicks,
        string ExecutableSha256);
}
