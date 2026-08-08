namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Maps a caller's sandbox-namespace path onto the container path and the host path that actually name the same
///     bytes, and rejects anything that escapes the workspace mount.
///     <para>
///         This mirrors <c>ProcessSandboxRuntimeProvider.ResolveJailPath</c> deliberately, because callers address
///         files the same way whichever provider is in force: a leading separator means "sandbox-absolute", and the
///         sandbox root is the workspace — not the container's <c>/</c>. Development Mode passes the literal
///         <c>"/"</c> as the working directory for every command, so a provider that forwarded that string unmapped
///         would run <em>every</em> command in the container root rather than in the repository. Divergence between
///         the two providers here is a security bug rather than a stylistic difference, which is why the escape
///         rejection throws the same <see cref="UnauthorizedAccessException" /> the process provider throws.
///     </para>
///     <para>
///         Container paths are POSIX regardless of what the engine host is — the engine may be a native Windows
///         process while the container is always Linux — so the container leg is normalised by this file rather
///         than by <see cref="Path" />, whose separator and rooting rules would answer for the wrong operating
///         system. Only the host leg uses <see cref="Path" />, and only after the container leg has proven
///         containment.
///     </para>
/// </summary>
internal static class DockerSandboxPaths
{
    /// <summary>The container's path separator, which is POSIX whatever the engine host is.</summary>
    private const char PosixSeparator = '/';

    /// <summary>
    ///     Canonicalises a sandbox path into the absolute in-container path under <paramref name="mountTarget" />.
    ///     Throws <see cref="UnauthorizedAccessException" /> for any path that escapes the mount, whether by
    ///     <c>..</c> traversal or by naming something outside it.
    /// </summary>
    internal static string ResolveContainerPath(string mountTarget, string sandboxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountTarget);
        ArgumentNullException.ThrowIfNull(sandboxPath);

        var root = NormalizePosix(mountTarget);

        // A leading separator makes the path sandbox-absolute, which means "relative to the workspace mount", not
        // "relative to the container root". Both separators are trimmed for parity with the process provider, which
        // trims both because a caller on Windows may compose a path with either.
        var relative = sandboxPath.TrimStart(PosixSeparator, '\\');
        var canonical = NormalizePosix(root + PosixSeparator + relative);

        if (!IsUnderPosixRoot(root, canonical))
        {
            throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the workspace mount and is rejected.");
        }

        return canonical;
    }

    /// <summary>
    ///     Canonicalises a sandbox path into the HOST path backing it — the mount source, not the container path.
    ///     Containment is proven twice: once in the container namespace and once again on the host after
    ///     <see cref="Path.GetFullPath(string)" />, because the two normalisations do not have to agree about a
    ///     component the host filesystem treats specially.
    /// </summary>
    internal static string ResolveHostPath(string workspaceRoot, string mountTarget, string sandboxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var root = NormalizePosix(mountTarget);
        var containerPath = ResolveContainerPath(mountTarget, sandboxPath);
        var relative = containerPath[root.Length..].TrimStart(PosixSeparator);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var combined = relative.Length == 0
            ? canonicalRoot
            : Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace(PosixSeparator, Path.DirectorySeparatorChar)));

        if (!IsUnderHostRoot(canonicalRoot, combined))
        {
            throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the workspace mount and is rejected.");
        }

        return combined;
    }

    /// <summary>
    ///     Collapses <c>.</c> and <c>..</c> in a POSIX path and returns it rooted. A <c>..</c> that would climb above
    ///     the root is dropped rather than honoured, matching <see cref="Path.GetFullPath(string)" /> — which is why
    ///     the containment check afterwards is the control, not this method.
    /// </summary>
    internal static string NormalizePosix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var segments = new List<string>();
        foreach (var segment in path.Split(PosixSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return PosixSeparator + string.Join(PosixSeparator, segments);
    }

    /// <summary>Whether <paramref name="candidate" /> is <paramref name="root" /> itself or lives under it.</summary>
    internal static bool IsUnderHostRoot(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(candidate, root, StringComparison.Ordinal)
               || candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsUnderPosixRoot(string root, string candidate)
    {
        var prefix = root.EndsWith(PosixSeparator) ? root : root + PosixSeparator;
        return string.Equals(candidate, root, StringComparison.Ordinal)
               || candidate.StartsWith(prefix, StringComparison.Ordinal);
    }
}
