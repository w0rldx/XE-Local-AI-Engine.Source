namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

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

    /// <summary>Publishes <paramref name="stagingDir" /> as <paramref name="targetDir" />.</summary>
    public static Task PublishAsync(string stagingDir, string targetDir, CancellationToken ct) =>
        PublishCoreAsync(stagingDir, targetDir, ct);

    /// <summary>The publish behind <see cref="PublishAsync" />; <paramref name="move" /> and <paramref name="delay" /> are test seams.</summary>
    internal static async Task PublishCoreAsync(string stagingDir, string targetDir, CancellationToken ct,
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
