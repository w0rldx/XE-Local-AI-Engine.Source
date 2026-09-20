namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;

internal static class DevelopmentWorkspaceSecurity
{
    private const char SandboxSeparator = '/';

    /// <summary>
    ///     The exceptions to <see cref="IsProtected" />'s dot-path rule, matched against a path's first segment
    ///     exactly or with a <c>.suffix</c> after it, so one <c>.env</c> entry covers <c>.env.example</c>.
    /// </summary>
    /// <remarks>
    ///     The suffix form requires the dot, so <c>.envrc</c> — another tool's state file — stays protected. All but
    ///     <c>.env</c> are tracked root dot-entries a red build is usually fixed in, which is why <c>.git</c> stays
    ///     closed while <c>.gitignore</c> and <c>.github/</c> stay open; a tool's index directory carrying a tracked
    ///     <c>.gitignore</c> is still a regenerable index and stays protected. <c>.env</c> is here because the secret
    ///     gate governs it more precisely, still allowing the creation a deny-list entry here would silently remove.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     The rule is the dot prefix itself, never a list of names, so the guard does not depend on which software a
    ///     contributor installed. The first segment alone decides it, the file scanner asking about a bare directory
    ///     name before descending. <see cref="Confine" /> refuses such a path as a tool argument and the listing and
    ///     search tools drop it from their output, so a root listing cannot spend its whole budget on Git internals.
    ///     <c>.xe-dev</c> is covered here but really guarded by the digest re-check, which a side effect cannot evade.
    /// </remarks>
    public static bool IsProtected(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        // Segment-aware by construction: ".gitignore" is its own first segment and never a match for ".git". Span
        // IndexOf because the string overload is culture-sensitive and this splitting must be ordinal (CA1307).
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

/// <summary>A Development workspace security or validation guard rejected the supplied value.</summary>
/// <remarks>
///     Endpoints whose request carries the rejected value map this to 400; endpoints acting purely on persisted
///     project state map it to 409. The state-conflict half of the family has its own type, see
///     <see cref="DevelopmentRepositoryStateConflictException" />.
/// </remarks>
public class DevelopmentWorkspaceSecurityException : InvalidOperationException
{
    public DevelopmentWorkspaceSecurityException(string message) : base(message) { }

    public DevelopmentWorkspaceSecurityException(string message, Exception innerException) : base(message, innerException) { }
}
