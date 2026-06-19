namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

/// <summary>
///     Deep host-path canonicalization for the sandbox workspace copy. It resolves the trusted root to its real
///     canonical path (following symlinks), rejects <c>\\?\</c>/<c>\\.\</c> extended/device paths, control characters,
///     and relative/traversal segments, and decides whether a reparse point (Windows junction/symlink, Linux symlink)
///     escapes the trusted root. Everything fails closed: an unresolvable path is treated as unsafe.
/// </summary>
internal static class HostPathSafety
{
    /// <summary>
    ///     Resolves the trusted selected-folder root to its real canonical path, or returns <see langword="null" />
    ///     when it cannot be resolved safely (missing, not a directory, extended/device path, control chars,
    ///     relative/traversal segments). The caller must fail closed on <see langword="null" />.
    /// </summary>
    public static string? TryResolveTrustedRoot(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath) || hostPath.Any(char.IsControl))
        {
            return null;
        }

        // Fail closed for extended-length / device namespaces that bypass normal path normalization.
        if (hostPath.StartsWith(@"\\?\", StringComparison.Ordinal) || hostPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(hostPath))
        {
            return null;
        }

        var segments = hostPath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        try
        {
            var info = new DirectoryInfo(hostPath);
            if (!info.Exists)
            {
                return null;
            }

            var target = info.ResolveLinkTarget(true);
            return Normalize((target ?? info).FullName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Resolves a reparse point's final target and reports whether it stays inside <paramref name="resolvedRoot" />.
    ///     Returns <see langword="false" /> (fail closed) when the target cannot be resolved.
    /// </summary>
    public static bool TryResolveReparseWithinRoot(FileSystemInfo info, string resolvedRoot, out bool withinRoot)
    {
        ArgumentNullException.ThrowIfNull(info);

        withinRoot = false;
        try
        {
            var target = info.ResolveLinkTarget(true);
            if (target is null)
            {
                return false;
            }

            withinRoot = IsPathWithinRoot(resolvedRoot, Normalize(target.FullName));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns whether <paramref name="candidateRealPath" /> is the root itself or sits beneath it.</summary>
    public static bool IsPathWithinRoot(string resolvedRoot, string candidateRealPath)
    {
        if (string.Equals(resolvedRoot, candidateRealPath, StringComparison.Ordinal))
        {
            return true;
        }

        return candidateRealPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Returns whether the entry is a reparse point (Windows junction/symlink or Linux symlink).</summary>
    public static bool IsReparsePoint(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
