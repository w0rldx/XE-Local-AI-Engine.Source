namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;

internal static class DevelopmentWorkspaceSecurity
{
    private const char SandboxSeparator = '/';
    private static readonly string[] ProtectedPrefixes = [".git", ".omx/ultragoal"];

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
        if (ProtectedPrefixes.Any(prefix => string.Equals(relative, prefix, StringComparison.Ordinal)
                                            || relative.StartsWith(prefix + "/", StringComparison.Ordinal)))
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
    public static DevelopmentConfinedPath Accepted(string relativePath, string sandboxPath) => new(true, relativePath, sandboxPath, null);
    public static DevelopmentConfinedPath Rejected(string reason) => new(false, string.Empty, string.Empty, reason);
}

public sealed class DevelopmentWorkspaceSecurityException(string message) : InvalidOperationException(message);
