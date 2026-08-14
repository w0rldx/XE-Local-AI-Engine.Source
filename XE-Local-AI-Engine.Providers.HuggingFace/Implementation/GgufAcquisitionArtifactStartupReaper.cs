namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Startup sweep for stale GGUF acquisition artifacts left behind by a crashed import/download: operation-owned
///     <c>*.part</c> files (weight and sidecar staging — see <see cref="GgufModelImporter" />,
///     <see cref="HuggingFaceGgufDownloadTransaction" />, and <see cref="HfDownloadClient" />) and orphaned final
///     <c>.xe-model.json</c> sidecars with no adjacent GGUF (a crash between the sidecar-first and weight renames of a
///     commit — see <see cref="GgufModelImporter.CommitAsync" />). Runs once at startup; best-effort, never blocks
///     node startup on a cleanup failure.
///     <para>
///         <b>Safety.</b> Never deletes a <c>.gguf</c> file — the two sweeps only ever match the <c>.part</c> and
///         <c>.xe-model.json</c> file-name patterns, which a real model weight never has. An artifact is only removed
///         once it is older than <see cref="StaleArtifactAge" />, so an acquisition genuinely in progress is untouched.
///     </para>
///     <para>
///         <b>Scope.</b> This does not reconcile the model-provider-map (an absent map row for a verified,
///         sidecar-backed entry); the resolver's default-provider fallback covers routing for that case.
///     </para>
/// </summary>
internal sealed class GgufAcquisitionArtifactStartupReaper(
    HuggingFaceOptions options,
    TimeProvider timeProvider,
    ILogger<GgufAcquisitionArtifactStartupReaper> logger) : IHostedService
{
    /// <summary>Conservative age threshold before a stale acquisition artifact is considered abandoned.</summary>
    internal static readonly TimeSpan StaleArtifactAge = TimeSpan.FromHours(24);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Sweep();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a cleanup failure must never block node startup.
            logger.LogWarning(exception, "Could not sweep stale GGUF acquisition artifacts at startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private void Sweep()
    {
        if (!Directory.Exists(options.ModelsDirectory))
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var path in Directory.EnumerateFiles(options.ModelsDirectory, "*.part", SearchOption.TopDirectoryOnly))
        {
            TryDeleteIfStale(path, now, "stale operation-owned .part file");
        }

        foreach (var path in Directory.EnumerateFiles(options.ModelsDirectory, "*" + GgufAcquisitionSidecar.Suffix, SearchOption.TopDirectoryOnly))
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
            logger.LogWarning("Reaped {Reason}: {FileName}.", reason, Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not reap stale GGUF acquisition artifact {FileName}.", Path.GetFileName(path));
        }
    }
}
