namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Startup hosted service that reaps stale <c>whisper-server</c> orphans left by a previous run of THIS app.
/// </summary>
/// <remarks>
///     The supervisor launches the daemon detached and tears it down only through its graceful shutdown; a hard kill of the host skips
///     that path, orphaning a daemon that still holds its loopback port and memory, so reaping on the next start is what makes a restart
///     reliable however the previous run died. <b>Strict matching:</b> a process is reaped ONLY when its executable path sits under this
///     app's own whisper.cpp binaries root, so an unrelated <c>whisper-server</c> is never touched, and an unresolvable root logs and
///     does nothing. Best-effort and wrapped throughout, and it runs before any request is served.
/// </remarks>
internal sealed class StaleWhisperServerReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly ILogger<StaleWhisperServerReaper> _logger;
    private readonly IStaleWhisperServerProcessScanner _scanner;

    /// <summary>
    ///     Creates the reaper over the process-scan seam. <paramref name="binariesRoot" /> is the directory under which
    ///     ONLY this app's whisper-server binaries live; a candidate outside it is never reaped, and a null or blank
    ///     root disables the reap.
    /// </summary>
    public StaleWhisperServerReaper(IStaleWhisperServerProcessScanner scanner,
        string? binariesRoot,
        ILogger<StaleWhisperServerReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _binariesRoot = binariesRoot;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The whole body is guarded: a reaper failure must NEVER block application start.
        try
        {
            Reap();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup whisper-server orphan reaper failed; continuing startup.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void Reap()
    {
        if (string.IsNullOrWhiteSpace(_binariesRoot))
        {
            _logger.LogInformation("Skipping whisper-server orphan reap: the runtime binaries directory could not be resolved.");
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot);
        var candidates = _scanner.EnumerateWhisperServerProcesses();

        var reaped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ExecutablePath is not { Length: > 0 } executablePath || !PathContainment.IsUnderRoot(executablePath, fullRoot))
            {
                continue;
            }

            _logger.LogInformation("Reaping stale whisper-server orphan (pid {Pid}).", candidate.Pid);
            _scanner.KillProcessTree(candidate.Pid);
            reaped++;
        }

        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale whisper-server orphan process(es) left by a previous run.", reaped);
        }
    }
}
