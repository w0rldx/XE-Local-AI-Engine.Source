namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;

internal static class DevelopmentWorkspaceSecurity
{
    private const char SandboxSeparator = '/';

    // ".xe-dev" holds the command-profile import source. Adding it here stops the agent naming it as a path argument
    // to a workspace tool, which is necessary but NOT sufficient: a build or test command can still write the file as
    // a side effect, entirely outside this check. The property is actually carried by the digest re-check in
    // DevelopmentWorkspaceTools.EnsureWorkspaceInvariantAsync plus the fact that the database, not the worktree, is
    // the source of truth for the profile. Do not treat this deny-list entry as the guard.
    private static readonly string[] ProtectedPrefixes = [".git", ".omx/ultragoal", ".xe-dev"];

    /// <summary>
    ///     The same prefixes <see cref="IsProtected" /> enforces, exposed so the listing tool can PRUNE these trees in
    ///     its <c>find</c> expression instead of only filtering them out afterwards. Reading the one array keeps the
    ///     generator and the filter from drifting apart; a second, hand-maintained copy at the call site would not.
    /// </summary>
    public static IReadOnlyList<string> ProtectedPathPrefixes => ProtectedPrefixes;

    public static string CanonicalRepositoryRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The trusted repository root does not exist.");
        }

        EnsureNoSymlinkComponents(canonical);
        return canonical;
    }

    public static string RepositoryIdentityHash(string canonicalRepositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRepositoryRoot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRepositoryRoot)));
    }

    public static DevelopmentConfinedPath Confine(string? path, bool allowRoot = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return allowRoot
                ? DevelopmentConfinedPath.Accepted(string.Empty, "/")
                : DevelopmentConfinedPath.Rejected("a workspace-relative file path is required.");
        }

        if (path.Any(char.IsControl)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return DevelopmentConfinedPath.Rejected("the path contains a forbidden control or device prefix.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            return DevelopmentConfinedPath.Rejected("absolute paths are not allowed.");
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        return DevelopmentConfinedPath.Rejected("the path traverses above the workspace root.");
                    }

                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        var relative = string.Join('/', segments);
        if (IsProtected(relative))
        {
            return DevelopmentConfinedPath.Rejected("the path targets protected engine or Git state.");
        }

        if (!allowRoot && relative.Length == 0)
        {
            return DevelopmentConfinedPath.Rejected("a workspace-relative file path is required.");
        }

        return DevelopmentConfinedPath.Accepted(relative,
            relative.Length == 0 ? SandboxSeparator.ToString() : string.Concat(SandboxSeparator, relative));
    }

    /// <summary>
    ///     Whether a workspace-relative path sits under one of the <see cref="ProtectedPrefixes" />. <see cref="Confine" />
    ///     uses it to refuse the path as a tool argument; the listing and search tools use it to drop the same paths
    ///     from their OUTPUT, so the policy reads the same way whether a path is asked for or merely enumerated.
    ///     <para>
    ///         Dropping them from listings is not cosmetic. A freshly cloned worktree's <c>.git</c> holds far more
    ///         entries than <see cref="DevelopmentOptions.MaxChangedFiles" /> allows a listing to return, so without
    ///         this a root <c>list_files</c> can spend its entire budget on Git internals the agent is forbidden to
    ///         open anyway, and return nothing it can act on.
    ///     </para>
    /// </summary>
    public static bool IsProtected(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Array.Exists(ProtectedPrefixes,
            prefix => string.Equals(relativePath, prefix, StringComparison.OrdinalIgnoreCase)
                      || relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureNoSymlinkComponents(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath) ?? throw new InvalidOperationException("The repository root could not be resolved.");
        var current = root;
        foreach (var segment in canonicalPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new DevelopmentWorkspaceSecurityException("The trusted repository path cannot traverse a symbolic link.");
            }
        }
    }
}

internal readonly record struct DevelopmentConfinedPath(bool IsAccepted, string RelativePath, string SandboxPath, string? RejectionReason)
{
    public static DevelopmentConfinedPath Accepted(string relativePath, string sandboxPath) =>
        new(true, relativePath, sandboxPath, null);

    public static DevelopmentConfinedPath Rejected(string reason) =>
        new(false, string.Empty, string.Empty, reason);
}

public sealed class DevelopmentWorkspaceSecurityException : InvalidOperationException
{
    public DevelopmentWorkspaceSecurityException(string message) : base(message) { }
}
