namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps stale <c>sd-server</c> orphans left by a previous run of THIS app.
///     The supervisor launches sd-server detached (Linux <c>setsid</c> / Windows Job Object) and tears it down only via
///     graceful DI shutdown (<see cref="ImageServerProcessSupervisor" />.<c>DisposeAsync</c>); a hard kill of the host
///     (e.g. <c>aspire stop</c>) skips that path, orphaning the daemon while it still holds its loopback port and GPU VRAM.
///     Reaping on the next start makes restart reliable regardless of how the previous run died. Mirrors
///     <c>StaleLlamaServerReaper</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Strict matching:</b> a process is reaped ONLY when its executable path is under the app's own
///         stable-diffusion.cpp binaries root
///         (<see cref="StableDiffusionCppBinaryManager.DefaultStableDiffusionBinariesRoot" />), so an unrelated
///         <c>sd-server</c> is never touched. When the root cannot be resolved the reaper logs and no-ops.
///     </para>
///     <para>
///         The whole reap is best-effort and wrapped so a reaper failure can never block app start. It runs before the
///         supervisor spawns any process — hosted services start during host startup, before requests are served — so it
///         only ever observes orphans from a previous run, never this run's own children.
///     </para>
/// </remarks>
internal sealed class StaleImageServerReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly ILogger<StaleImageServerReaper> _logger;
    private readonly IStaleImageServerProcessScanner _scanner;

    /// <summary>
    ///     Creates the reaper over the process-scan seam. <paramref name="binariesRoot" /> is the directory under which
    ///     ONLY this app's sd-server binaries live; a candidate outside it is never reaped. A <see langword="null" />
    ///     or blank root disables the reap (logged, no-op).
    /// </summary>
    public StaleImageServerReaper(IStaleImageServerProcessScanner scanner,
        string? binariesRoot,
        ILogger<StaleImageServerReaper> logger)
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
        // The whole body is guarded: a reaper failure must NEVER block application start. The synchronous OS process
        // scan is wrapped here rather than offloaded — it is a quick one-shot at startup before any request is served.
        try
        {
            Reap();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup sd-server orphan reaper failed; continuing startup.");
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
            _logger.LogInformation("Skipping sd-server orphan reap: the runtime binaries directory could not be resolved.");
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot);
        var candidates = _scanner.EnumerateImageServerProcesses();

        var reaped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ExecutablePath is not { Length: > 0 } executablePath || !IsUnderRoot(executablePath, fullRoot))
            {
                continue;
            }

            _logger.LogInformation("Reaping stale sd-server orphan (pid {Pid}) at {Path}.", candidate.Pid, executablePath);
            _scanner.KillProcessTree(candidate.Pid);
            reaped++;
        }

        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale sd-server orphan process(es) left by a previous run.", reaped);
        }
    }

    /// <summary>
    ///     <see langword="true" /> when <paramref name="executablePath" /> is a descendant of <paramref name="root" />.
    ///     Both are normalized to a full path; the comparison is case-insensitive on Windows. The trailing
    ///     directory-separator guard prevents a sibling-prefix false match (e.g. <c>.../stable-diffusion.cpp-other/sd-server</c>
    ///     must not match the root <c>.../stable-diffusion.cpp</c>).
    /// </summary>
    private static bool IsUnderRoot(string executablePath, string root)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (ArgumentException)
        {
            // An unparseable path can never be under our root.
            return false;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(rootWithSeparator, comparison);
    }
}
