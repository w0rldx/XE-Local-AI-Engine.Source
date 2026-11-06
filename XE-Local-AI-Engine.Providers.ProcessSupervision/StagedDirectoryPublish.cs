namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Moves a COMPLETE staged runtime tree into its live directory, replacing any previous content, so a resolve never
///     observes a partial install and a failed publish never leaves no runtime.
/// </summary>
/// <remarks>
///     The old dir is renamed aside, not deleted first, so a failure never leaves a half-deleted or no runtime: a failed move renames it back, then rethrows.
///     Both renames retry on <see cref="RenameRetryDelays" />: on Windows an antivirus or indexer handle on fresh files makes a rename fail with access denied for a moment.
///     The aside is <c>.aside</c>, never <c>.tmp</c>: a rename keeps the old mtime, so a staging sweep's age bound could not protect it.
/// </remarks>
public static class StagedDirectoryPublish
{
    /// <summary>The pause before each retry of a publish rename; a rename is tried once more than there are delays.</summary>
    internal static readonly IReadOnlyList<TimeSpan> RenameRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    /// <summary>Publishes <paramref name="stagingDir" /> as <paramref name="targetDir" />, then sweeps stray asides of earlier publishes.</summary>
    public static Task PublishAsync(string stagingDir, string targetDir, ILogger logger, CancellationToken ct) =>
        PublishCoreAsync(stagingDir, targetDir, logger, ct);

    /// <summary>The publish behind <see cref="PublishAsync" />; <paramref name="move" /> and <paramref name="delay" /> are test seams.</summary>
    /// <remarks>Accepted ceiling: the retry pause defaults to the real clock, the type has no TimeProvider in reach; upgrade path: thread the callers' TimeProvider.</remarks>
    internal static async Task PublishCoreAsync(string stagingDir, string targetDir, ILogger logger, CancellationToken ct,
        Action<string, string>? move = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        move ??= Directory.Move;
        delay ??= Task.Delay;
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir.TrimEnd(Path.DirectorySeparatorChar))!);
        string? aside = null;
        if (Directory.Exists(targetDir))
        {
            aside = $"{targetDir}.{Guid.NewGuid():N}.aside";
            await RenameWithRetryAsync(targetDir, aside, move, delay, ct).ConfigureAwait(false);
        }

        try
        {
            await RenameWithRetryAsync(stagingDir, targetDir, move, delay, ct).ConfigureAwait(false);
        }
        catch (Exception moveIn) when (aside is not null)
        {
            try
            {
                move(aside, targetDir);
            }
            catch (Exception restore)
            {
                // The target dir is gone; the old tree's location goes to the log with this exception (never to a user-facing message). A later ensure re-acquires.
                throw new IOException($"Publishing '{targetDir}' failed and restoring its previous contents failed too; they remain at '{aside}'.",
                    new AggregateException(moveIn, restore));
            }

            throw;
        }

        if (aside is not null)
        {
            BestEffortFile.TryDeleteDirectory(aside);
        }

        SweepStrayAsides(targetDir, logger);
    }

    // An aside a crashed or failed publish left behind holds a superseded tree; once the new tree is live it only costs disk.
    private static void SweepStrayAsides(string targetDir, ILogger logger)
    {
        var trimmed = targetDir.TrimEnd(Path.DirectorySeparatorChar);
        var prefixLength = Path.GetFileName(trimmed).Length + 1;
        foreach (var stray in Directory.EnumerateDirectories(Path.GetDirectoryName(trimmed)!, $"{Path.GetFileName(trimmed)}.*.aside"))
        {
            // Only this target's own "{target}.{guid:N}.aside", never the aside of a sibling target whose name has a dot.
            if (!Guid.TryParseExact(Path.GetFileName(stray)[prefixLength..^".aside".Length], "N", out _))
            {
                continue;
            }

            logger.LogInformation("Removing the leftover publish aside {AsideDir}.", stray);
            BestEffortFile.TryDeleteDirectory(stray);
        }
    }

    // A missing source never appears, so DirectoryNotFoundException fails at once; other IO and access errors get one more try per delay.
    private static async Task RenameWithRetryAsync(string source, string destination, Action<string, string> move, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        foreach (var pause in RenameRetryDelays)
        {
            try
            {
                move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) and not DirectoryNotFoundException)
            {
                await delay(pause, ct).ConfigureAwait(false);
            }
        }

        move(source, destination);
    }
}
