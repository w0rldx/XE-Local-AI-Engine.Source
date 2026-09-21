namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Startup sweep for stale GGUF acquisition artifacts left behind by a crashed import/download: operation-owned
///     <c>*.part</c> staging files and orphaned final <c>.xe-model.json</c> sidecars with no adjacent GGUF.
/// </summary>
/// <remarks>
///     The <c>.part</c> writers are <see cref="GgufModelImporter" />, <see cref="HuggingFaceGgufDownloadTransaction" /> and <see cref="HfDownloadClient" />;
///     an orphan means a crash between the sidecar-first and weight renames of a commit (<see cref="GgufModelImporter.CommitAsync" />).
///     Runs once at startup, best-effort, never blocking startup on a cleanup failure. <b>Safety.</b> Never deletes a <c>.gguf</c> file —
///     no real weight carries those patterns — and only removes an artifact older than <see cref="StaleArtifactAge" />, so a live
///     acquisition is untouched. <b>Scope.</b> It does not reconcile the model-provider-map; the default-provider fallback covers that.
/// </remarks>
internal sealed class GgufAcquisitionArtifactStartupReaper : IHostedService
{
    /// <summary>Conservative age threshold before a stale acquisition artifact is considered abandoned.</summary>
    internal static readonly TimeSpan StaleArtifactAge = TimeSpan.FromHours(24);
    private readonly HuggingFaceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GgufAcquisitionArtifactStartupReaper> _logger;

    public GgufAcquisitionArtifactStartupReaper(
        HuggingFaceOptions options,
        TimeProvider timeProvider,
        ILogger<GgufAcquisitionArtifactStartupReaper> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Sweep();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a cleanup failure must never block node startup.
            _logger.LogWarning(exception, "Could not sweep stale GGUF acquisition artifacts at startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private void Sweep()
    {
        if (!Directory.Exists(_options.ModelsDirectory))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var path in Directory.EnumerateFiles(_options.ModelsDirectory, "*.part", SearchOption.TopDirectoryOnly))
        {
            TryDeleteIfStale(path, now, "stale operation-owned .part file");
        }

        foreach (var path in Directory.EnumerateFiles(_options.ModelsDirectory, "*" + GgufAcquisitionSidecar.Suffix, SearchOption.TopDirectoryOnly))
        {
            var weightPath = path[..^GgufAcquisitionSidecar.Suffix.Length];
            if (File.Exists(weightPath))
            {
                continue;
            }

            TryDeleteIfStale(path, now, "orphaned acquisition sidecar with no adjacent GGUF");
        }
    }

    private void TryDeleteIfStale(string path, DateTime nowUtc, string reason)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;
        }

        if (!info.Exists || nowUtc - info.LastWriteTimeUtc < StaleArtifactAge)
        {
            return;
        }

        try
        {
            File.Delete(path);
            _logger.LogWarning("Reaped {Reason}: {FileName}.", reason, Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not reap stale GGUF acquisition artifact {FileName}.", Path.GetFileName(path));
        }
    }
}
