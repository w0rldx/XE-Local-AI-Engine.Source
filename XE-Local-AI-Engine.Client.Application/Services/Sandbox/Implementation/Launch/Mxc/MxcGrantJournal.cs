namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The ENGINE-OWNED paths MXC was asked to grant (the jail, the compute venv), recorded BEFORE each spawn so a startup sweep after a
///     crash knows exactly where DACL residue can be.
/// </summary>
/// <remarks>
///     Exact rather than guessed: the granted paths sit at different depths (a jail two levels under the container root, a venv under
///     the compute cache), and a top-level sweep only finds residue on a path it is given. Only paths under <see cref="EngineOwnedRoots" />
///     are recorded or drained: an operator-supplied tree (an MCP server directory, a trusted host workspace) may carry another
///     application's legitimate AppContainer ACEs, which the sweep cannot tell apart from residue. Best-effort: an unwritable journal is logged and the launch proceeds, since <c>ClearPolicyOnExit</c> still removes the grants on a
///     normal exit.
/// </remarks>
// One in-process lock; two engine processes appending at once can lose a line. Per-process journal files if that ever matters.
public static class MxcGrantJournal
{
    private static readonly Lock Gate = new();

    /// <summary>The journal beside the jails under <see cref="SandboxPaths.ContainerRoot" />.</summary>
    public static string DefaultPath => Path.Combine(SandboxPaths.ContainerRoot, "mxc-grants.journal");

    /// <summary>The roots the engine creates and owns: the jails under the container root and the compute venv directory.</summary>
    public static IReadOnlyList<string> EngineOwnedRoots => [SandboxPaths.ContainerRoot, Path.Combine(ComputeRuntimeDirectory.DefaultCacheRoot(), "venv")];

    /// <summary>Appends the <paramref name="paths" /> under one of <paramref name="ownedRoots" />; every other path is dropped.</summary>
    public static void Record(string journalPath, IEnumerable<string> paths, IReadOnlyList<string> ownedRoots, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ownedRoots);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            lock (Gate)
            {
                if (Path.GetDirectoryName(journalPath) is { Length: > 0 } directory)
                {
                    _ = Directory.CreateDirectory(directory);
                }

                File.AppendAllLines(journalPath, paths.Where(path => IsEngineOwned(path, ownedRoots)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not record the MXC grant journal at {Journal}; a crash before cleanup would leave residue unswept.", journalPath);
        }
    }

    /// <summary>
    ///     Reads every recorded path under one of <paramref name="ownedRoots" /> once (distinct, in order) and removes the journal. A line
    ///     outside them (a journal written before the scope was narrowed) is dropped, never swept. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Drain(string journalPath, IReadOnlyList<string> ownedRoots, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ownedRoots);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            lock (Gate)
            {
                if (!File.Exists(journalPath))
                {
                    return [];
                }

                var paths = File.ReadAllLines(journalPath).Where(line => IsEngineOwned(line, ownedRoots)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                File.Delete(journalPath);
                return paths;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the MXC grant journal at {Journal}.", journalPath);
            return [];
        }
    }

    private static bool IsEngineOwned(string path, IReadOnlyList<string> ownedRoots) =>
        !string.IsNullOrWhiteSpace(path) && ownedRoots.Any(root => PathContainment.IsUnderRoot(path, root));
}
