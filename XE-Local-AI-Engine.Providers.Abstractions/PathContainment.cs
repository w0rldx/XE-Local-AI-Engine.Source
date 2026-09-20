namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The one path-containment rule behind every "is this path under a root the node owns?" gate: the stale
///     server reapers, the sandbox orphan reaper, the image store. Each gate destroys something, so a second
///     copy silently downgrades one.
/// </summary>
public static class PathContainment
{
    /// <summary>
    ///     <see langword="true" /> when <paramref name="path" /> is a descendant of <paramref name="root" />, and
    ///     never for the root itself. Both sides are normalized first, so a <c>..</c> segment cannot walk out
    ///     behind the comparison, which is case-insensitive only on Windows.
    /// </summary>
    /// <remarks>
    ///     Purely lexical: symlinks are not resolved, so a link planted inside the root reads as contained however
    ///     far outside it points. Every caller judges a path the node itself produced under its own data directory.
    ///     Fails CLOSED — anything <see cref="Path.GetFullPath(string)" /> refuses answers
    ///     <see langword="false" /> rather than throwing out of the sweep that asked. Whitespace is a legal Unix
    ///     file name and is not refused: it resolves as a relative path and then fails to be under the root.
    /// </remarks>
    public static bool IsUnderRoot(string path, string root)
    {
        string fullPath;
        string fullRoot;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path can never be under our root.
            return false;
        }

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // The length check keeps the ROOT ITSELF out — a gate that DELETES or KILLS would otherwise take the whole
        // root as a target — and the trailing separator stops ".../llama.cpp-other" matching root ".../llama.cpp".
        return fullPath.Length > rootWithSeparator.Length && fullPath.StartsWith(rootWithSeparator, comparison);
    }
}
