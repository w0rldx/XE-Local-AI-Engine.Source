namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Path-safety guards for Hugging-Face-supplied file names (<c>rfilename</c>/<c>path</c>). A repo is untrusted
///     input: a malicious or compromised repo could return a name like <c>../../etc/evil-Q4_K_M.gguf</c> or a rooted
///     path. Discovery filters such names out, and the store re-checks containment before writing — defense in depth so
///     a download can never land outside the configured models directory.
/// </summary>
public static class GgufFilePath
{
    /// <summary>
    ///     True if <paramref name="fileName" /> is a safe repo-relative path: non-empty, not rooted, and free of any
    ///     <c>.</c>/<c>..</c> traversal segment (handling both <c>/</c> and <c>\</c> separators).
    /// </summary>
    public static bool IsSafeRelativePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (Path.IsPathRooted(fileName))
        {
            return false;
        }

        // Normalize both separators then reject any traversal ('..') or no-op ('.') segment.
        var segments = fileName.Replace('\\', '/').Split('/');
        return !segments.Any(static segment => segment is ".." or ".");
    }

    /// <summary>
    ///     Resolves the absolute path for <paramref name="fileName" /> under <paramref name="baseDirectory" />, throwing
    ///     when the result would escape the base directory. Use this immediately before opening any file handle.
    /// </summary>
    /// <exception cref="ArgumentException">The file name escapes <paramref name="baseDirectory" />.</exception>
    public static string ResolveContainedPath(string baseDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!IsSafeRelativePath(fileName))
        {
            throw new ArgumentException("The model file name is not a safe relative path.", nameof(fileName));
        }

        var baseFull = Path.GetFullPath(baseDirectory);
        var baseWithSeparator = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(baseFull, fileName));

        // Ordinal containment: a stricter (case-sensitive) comparison can only over-reject, never permit an escape.
        if (!combined.StartsWith(baseWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("The model file name resolves outside the models directory.", nameof(fileName));
        }

        return combined;
    }
}
