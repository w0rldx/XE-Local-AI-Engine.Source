namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;

internal static class DevelopmentWorkspaceSecurity
{
    private const char SandboxSeparator = '/';

    // The exceptions to the dot-path rule in IsProtected, matched against a path's first segment either exactly or
    // with a ".suffix" after them, so one ".env" entry covers ".env.example" and ".env.local" without listing every
    // variant a repository might carry. The suffix form deliberately requires the dot: ".envrc" is another tool's
    // state file and stays protected.
    //
    // All but ".env" are this repository's own tracked root dot-entries. Dev Mode exists partly to fix a red build,
    // and a red build is usually a workflow or an ignore rule, so ".git" stays closed while ".gitignore" and
    // ".github/" stay open. Being tracked is necessary but not sufficient: a tool's index directory that carries a
    // tracked ".gitignore" is still a regenerable index, not source, and stays protected.
    //
    // ".env" is here for a different reason and must not be read as "'.env' is safe". It is governed by the SECRET
    // gate (ISensitiveFileExclusionService), which is both stronger and more precise than this one: it refuses the
    // read, suppresses the file from every listing, and refuses a rename whose SOURCE is a secret, while still
    // allowing a creation — writing a fresh ".env.example" has no secret source to leak and is ordinary work.
    // Swallowing ".env*" into this deny-list would add no protection the secret gate does not already give and would
    // silently delete that last behaviour.
    private static readonly string[] EditableDotPaths = [".dockerignore", ".editorconfig", ".env", ".gitattributes", ".github", ".gitignore"];

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
    ///     Whether a workspace-relative path's FIRST segment is a dot-entry outside <see cref="EditableDotPaths" />.
    ///     The first segment alone decides it, which is what makes the prune correct: <c>WorkspaceFileScanner</c> asks
    ///     about a bare directory name before descending, so <c>.git</c> with nothing after it has to answer true.
    ///     <see cref="Confine" /> uses it to refuse the path as a tool argument; the listing and search tools use it to
    ///     drop the same paths from their OUTPUT, so the policy reads the same way whether a path is asked for or
    ///     merely enumerated.
    ///     <para>
    ///         The rule is the dot prefix itself, not a list of names. Git internals, this engine's own
    ///         <c>.xe-dev</c> import source and whatever state directory a contributor's editor or agent tooling drops
    ///         in the worktree are indistinguishable to the guard and equally none of the agent's business — and a
    ///         deny-list naming particular tools would protect nothing for a contributor who uses a different one,
    ///         which is a guard whose strength depends on which software happens to be installed.
    ///     </para>
    ///     <para>
    ///         <c>.xe-dev</c> is covered here, and it is worth saying why this is NOT the guard for it: refusing the
    ///         path as a tool argument is necessary but not sufficient, because a build or test command can still write
    ///         the file as a side effect, entirely outside this check. The property is actually carried by the digest
    ///         re-check in <c>DevelopmentWorkspaceTools.EnsureWorkspaceInvariantAsync</c> plus the fact that the
    ///         database, not the worktree, is the source of truth for the profile.
    ///     </para>
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

        // Segment-aware by construction: ".gitignore" is its own first segment and never a match for ".git".
        // Span IndexOf, because the string overload is culture-sensitive by default and CA1307 rejects it; segment
        // splitting here must be ordinal.
        var path = relativePath.AsSpan();
        var separator = path.IndexOf(SandboxSeparator);
        var firstSegment = separator < 0 ? path : path[..separator];

        // The empty path is the workspace root, which a root listing has to be allowed to name.
        if (firstSegment.IsEmpty || firstSegment[0] != '.')
        {
            return false;
        }

        foreach (var editable in EditableDotPaths)
        {
            if (firstSegment.Equals(editable, StringComparison.OrdinalIgnoreCase)
                || (firstSegment.Length > editable.Length
                    && firstSegment[editable.Length] == '.'
                    && firstSegment[..editable.Length].Equals(editable, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
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

/// <summary>
///     A Development workspace security/validation guard rejected the supplied value. Endpoints whose request carries
///     the rejected value map this to 400; endpoints that act purely on persisted project state map it to 409. The
///     state-conflict half of the family has its own type — see <see cref="DevelopmentRepositoryStateConflictException" />.
/// </summary>
public class DevelopmentWorkspaceSecurityException : InvalidOperationException
{
    public DevelopmentWorkspaceSecurityException(string message) : base(message) { }

    public DevelopmentWorkspaceSecurityException(string message, Exception innerException) : base(message, innerException) { }
}
