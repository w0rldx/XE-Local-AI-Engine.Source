namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The preview's advisory read of what the operator's own folders already hold.
/// </summary>
/// <remarks>
///     Informational throughout: it never decides <c>CanApply</c>, never enters the patch hash, and never turns a
///     good preview into a failure. A folder that is no git work tree has nothing to compare against and reports
///     nothing; a folder whose state cannot be read says so, which is a different answer from "clean".
/// </remarks>
internal sealed partial class NodePatchApplyService
{
    /// <summary>Keeps one status invocation's pathspec list under the shortest platform command-line limit.</summary>
    private const int MaxStatusArgumentBytes = 24 * 1024;

    private const int MaxStatusOutputBytes = 1024 * 1024;

    private const int MaxStatusErrorBytes = 4096;

    private const string GitDirectoryName = ".git";

    /// <summary>The answer to "where does this folder sit in its repository", including "that could not be read".</summary>
    private readonly record struct WorkTreeProbe
    {
        /// <summary>The repository-root-relative prefix, or <see langword="null" /> when there is no work tree here.</summary>
        public string? Prefix { get; private init; }

        /// <summary>Whether the question itself failed, which is not the same answer as "no repository".</summary>
        public bool Unavailable { get; private init; }

        public static WorkTreeProbe Unknown =>
            new()
            {
                Unavailable = true
            };

        public static WorkTreeProbe NoWorkTree => new();

        public static WorkTreeProbe At(string prefix) =>
            new()
            {
                Prefix = prefix
            };
    }

    private static async Task<(IReadOnlyList<PatchApplyDirtyEntry> Entries, bool Unavailable)> ReadDirtyTargetsAsync(HostGitRunner runner,
        IReadOnlyList<AliasPlan> aliases,
        CancellationToken cancellationToken)
    {
        var entries = new List<PatchApplyDirtyEntry>();
        var unavailable = false;
        foreach (var alias in aliases)
        {
            var probe = await ReadWorkTreePrefixAsync(runner, alias, cancellationToken);
            if (probe.Unavailable)
            {
                unavailable = true;
                continue;
            }

            if (probe.Prefix is not { } prefix)
            {
                continue;
            }

            if (!await AppendDirtyEntriesAsync(runner, alias, prefix, entries, cancellationToken))
            {
                unavailable = true;
            }
        }

        return (entries, unavailable);
    }

    /// <summary>
    ///     Where the alias root sits inside its repository, and whether that question could be answered at all.
    /// </summary>
    /// <remarks>
    ///     Git says "not a git repository" both for a folder that has none and for one whose <c>.git</c> it cannot
    ///     read, and the sentence is localizable, so neither the exit code nor the message tells them apart. Two
    ///     other signals do: a runner-level failure (timeout, no git, output past its bound) is no answer about
    ///     repositories at all, and a refusal beside a <c>.git</c> that exists on disk is one git declined to read.
    ///     Both report UNKNOWN, the safe direction, because silence here would read as "clean".
    /// </remarks>
    private static async Task<WorkTreeProbe> ReadWorkTreePrefixAsync(HostGitRunner runner, AliasPlan alias, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(alias.ResolvedRoot,
            AgentHomeGit.Arguments("rev-parse", "--is-inside-work-tree", "--show-prefix"),
            cancellationToken,
            maxStandardOutputBytes: MaxStatusErrorBytes,
            maxStandardErrorBytes: MaxStatusErrorBytes);
        if (result.ExitCode != 0)
        {
            var looksLikeARepository = Path.Exists(Path.Combine(alias.ResolvedRoot, GitDirectoryName));
            return result.ExitCode < 0 || looksLikeARepository ? WorkTreeProbe.Unknown : WorkTreeProbe.NoWorkTree;
        }

        // A bare repository answers "false": it has no work tree, so it has no local drift to report either.
        var lines = result.StandardOutput.Split('\n');
        if (!string.Equals(lines[0].Trim(), "true", StringComparison.Ordinal))
        {
            return WorkTreeProbe.NoWorkTree;
        }

        return WorkTreeProbe.At(lines.Length >= 2 ? lines[1].Trim() : string.Empty);
    }

    /// <summary>
    ///     Reads the host state of this alias's own patch targets and appends the ones that differ.
    /// </summary>
    /// <returns><see langword="false" /> when git could not answer, so the result is incomplete rather than clean.</returns>
    /// <remarks>
    ///     <c>--no-optional-locks</c> keeps a preview a read: <c>status</c> otherwise rewrites the operator's index.
    ///     <c>--literal-pathspecs</c> keeps it BOUNDED, these paths being model-authored: a <c>*</c> or a leading
    ///     <c>:</c> would otherwise be magic that walks the whole repository. It rides the arguments rather than
    ///     <c>GIT_LITERAL_PATHSPECS</c> because the environment is shared with every call, apply included.
    ///     <c>--ignore-submodules=all</c> keeps the preview out of a submodule the operator already had.
    /// </remarks>
    private static async Task<bool> AppendDirtyEntriesAsync(HostGitRunner runner,
        AliasPlan alias,
        string prefix,
        List<PatchApplyDirtyEntry> entries,
        CancellationToken cancellationToken)
    {
        var targets = new HashSet<string>(alias.TargetRelativePaths, StringComparer.Ordinal);
        foreach (var chunk in ChunkPathspecs(alias.TargetRelativePaths))
        {
            var result = await runner.RunAsync(alias.ResolvedRoot,
                AgentHomeGit.Arguments([
                    "--no-optional-locks",
                    "--literal-pathspecs",
                    "status",
                    "--porcelain=v1",
                    "-z",
                    "--untracked-files=all",
                    "--ignore-submodules=all",
                    "--",
                    .. chunk
                ]),
                cancellationToken,
                maxStandardOutputBytes: MaxStatusOutputBytes,
                maxStandardErrorBytes: MaxStatusErrorBytes);
            if (result.ExitCode != 0)
            {
                return false;
            }

            AppendStatusEntries(result.StandardOutput, alias.Alias, prefix, targets, entries);
        }

        return true;
    }

    /// <summary>
    ///     Reads <c>--porcelain=v1 -z</c> records: two status characters, a space, then the repository-relative path,
    ///     NUL-terminated and never C-quoted.
    /// </summary>
    private static void AppendStatusEntries(string output,
        string alias,
        string prefix,
        HashSet<string> targets,
        List<PatchApplyDirtyEntry> entries)
    {
        var fields = output.Split('\0');
        var index = 0;
        while (index < fields.Length)
        {
            var field = fields[index];
            index++;
            if (field.Length < 4)
            {
                continue;
            }

            var indexStatus = field[0];
            var state = DirtyState(indexStatus, field[1]);
            var paths = new List<string>
            {
                field[3..]
            };
            if (indexStatus is 'R' or 'C' && index < fields.Length)
            {
                // A staged rename or copy carries its ORIGIN in a second field rather than a record of its own.
                // Either side can be a patch target, and reading the origin as a record would mis-frame the rest.
                paths.Add(fields[index]);
                index++;
            }

            foreach (var path in paths)
            {
                if (TryStripPrefix(path, prefix) is { } relative && targets.Contains(relative))
                {
                    entries.Add(new PatchApplyDirtyEntry
                    {
                        Path = Describe(alias, relative),
                        State = state
                    });
                }
            }
        }
    }

    private static string DirtyState(char indexStatus, char workTreeStatus)
    {
        if (indexStatus == '?' && workTreeStatus == '?')
        {
            return "untracked";
        }

        return indexStatus == ' ' ? "modified" : "staged";
    }

    private static string? TryStripPrefix(string repositoryRelativePath, string prefix)
    {
        if (prefix.Length == 0)
        {
            return repositoryRelativePath;
        }

        return repositoryRelativePath.StartsWith(prefix, StringComparison.Ordinal) ? repositoryRelativePath[prefix.Length..] : null;
    }

    private static IEnumerable<List<string>> ChunkPathspecs(IReadOnlyList<string> paths)
    {
        var chunk = new List<string>();
        var bytes = 0;
        foreach (var path in paths)
        {
            if (chunk.Count > 0 && bytes + path.Length + 1 > MaxStatusArgumentBytes)
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }

            chunk.Add(path);
            bytes += path.Length + 1;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }
}
