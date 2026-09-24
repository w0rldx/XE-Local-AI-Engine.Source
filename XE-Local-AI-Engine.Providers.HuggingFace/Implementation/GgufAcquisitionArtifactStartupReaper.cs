namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Startup sweep that deletes GGUF acquisition leftovers: stale <c>*.part</c> files, download partials of an installed
///     file, and orphaned <c>.xe-model.json</c> sidecars.
/// </summary>
/// <remarks>
///     Writers: <see cref="GgufModelImporter" />, <see cref="HuggingFaceGgufDownloadTransaction" />, <see cref="HfDownloadClient" />.
///     Best-effort, never blocks startup, never deletes a <c>.gguf</c>. An artifact goes only when older than
///     <see cref="StaleArtifactAge" /> or when it is a download leftover whose final <c>.gguf</c> exists, which no live download
///     can own. It does not reconcile the model-provider-map. Rules: docs/wiki/07-model-fit.md.
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
            if (IsDownloadLeftoverOfInstalledFile(path))
            {
                TryDelete(path, "download partial of an already installed file");
                continue;
            }

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

        TryDelete(path, reason);
    }

    /// <summary>
    ///     Whether <paramref name="path" /> is a download partial, cursor or staged file (<c>&lt;name&gt;.gguf.…part</c>, not a
    ///     sidecar temp) whose final <c>&lt;name&gt;.gguf</c> is already installed.
    /// </summary>
    private static bool IsDownloadLeftoverOfInstalledFile(string path)
    {
        var name = Path.GetFileName(path);
        var end = name.IndexOf(".gguf.", StringComparison.OrdinalIgnoreCase);
        if (end < 0 || name.Contains(GgufAcquisitionSidecar.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var finalPath = Path.Combine(Path.GetDirectoryName(path)!, name[..(end + ".gguf".Length)]);
        return File.Exists(finalPath);
    }

    private void TryDelete(string path, string reason)
    {
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
