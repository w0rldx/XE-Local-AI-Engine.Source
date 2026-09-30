namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
        ExecutableIdentitySnapshot snapshot = new()
        {
            LengthBytes = 0,
            LastWriteUtc = DateTimeOffset.UnixEpoch
        };
        try
        {
            snapshot = ReadIdentitySnapshot(binary.ServerExecutablePath);
            var executableSha256 = await ComputeSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false);
            var key = new CapabilityCacheKey
            {
                Variant = binary.Variant,
                RequestedVersion = binary.Version,
                ExecutablePath = Path.GetFullPath(binary.ServerExecutablePath),
                LengthBytes = snapshot.LengthBytes,
                LastWriteUtcTicks = snapshot.LastWriteUtc.UtcTicks,
                ExecutableSha256 = executableSha256
            };
            var gate = _probeGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A waiter may have observed the old file immediately before a replacement, so re-read identity after acquiring the key gate and fail this attempt
                // if it changed. A later caller probes the new identity; never recurse while holding the old identity's single-flight gate.
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
                ? new ExecutableIdentitySnapshot
                {
                    LengthBytes = info.Length,
                    LastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                }
                : new ExecutableIdentitySnapshot
                {
                    LengthBytes = 0,
                    LastWriteUtc = DateTimeOffset.UnixEpoch
                };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ExecutableIdentitySnapshot
            {
                LengthBytes = 0,
                LastWriteUtc = DateTimeOffset.UnixEpoch
            };
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

    private sealed record ExecutableIdentitySnapshot
    {
        public required long LengthBytes { get; init; }

        public required DateTimeOffset LastWriteUtc { get; init; }
    }

    private sealed record CapabilityCacheKey
    {
        public required GpuVariant Variant { get; init; }

        public required string RequestedVersion { get; init; }

        public required string ExecutablePath { get; init; }

        public required long LengthBytes { get; init; }

        public required long LastWriteUtcTicks { get; init; }

        public required string ExecutableSha256 { get; init; }
    }
}
