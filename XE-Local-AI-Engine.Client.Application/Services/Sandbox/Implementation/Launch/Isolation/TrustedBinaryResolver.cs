namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Resolves the helper binaries the isolated launch chain execs — <c>setsid</c>, <c>systemd-run</c>,
///     <c>systemctl</c>, <c>bwrap</c> — to an absolute, canonical, ROOT-OWNED path, or to nothing at all.
///     <para>
///         <b>PATH is not consulted.</b> Not "consulted last": not consulted. The containment probe's existing
///         <c>ResolveBinary</c> prefers <c>PATH</c> because a service manager can hand the worker a minimal one, and
///         for the resource-limit chain that is an availability question. For the FILESYSTEM boundary it is a trust
///         question, and the answer has to be a binary the engine's own user cannot have written: the chain's whole
///         job is to keep a sandboxed workload away from the host filesystem, and a workload that could drop a
///         <c>bwrap</c> earlier on <c>PATH</c> than the real one would be choosing the program that builds its own
///         jail. Only <see cref="TrustedRoots" /> are searched.
///     </para>
///     <para>
///         Trust is a property of the whole path, not of the leaf. Every component — the leaf included — must be owned
///         by uid 0 and must not be group- or world-writable, because a writable directory anywhere along the way is a
///         place to swap the binary. Symlinks are allowed (this is how a usr-merged distribution presents
///         <c>/bin</c>), but only when the CANONICAL target satisfies the same rule; a symlink's own mode bits are
///         ignored because on Linux they are always <c>0777</c> and mean nothing. What is returned is the canonical
///         path, so the chain execs the file that was validated rather than a name that could be re-pointed afterwards.
///     </para>
/// </summary>
public static class TrustedBinaryResolver
{
    /// <summary>
    ///     The only directories searched. <c>/usr/sbin</c> and <c>/sbin</c> are deliberately absent: none of the four
    ///     helpers lives there, and every directory listed is one more place that has to stay root-owned.
    /// </summary>
    public static readonly string[] TrustedRoots =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin"
    ];

    // A symlink chain longer than this is a loop or an attack, not a distribution layout.
    private const int MaximumSymbolicLinkDepth = 8;

    /// <summary>
    ///     Resolves <paramref name="name" /> against <see cref="TrustedRoots" />, returning the canonical path or
    ///     <see langword="null" /> when no root holds a trustworthy copy. Never throws: an unresolvable helper means
    ///     the capability is unavailable, which is a measurement, not an error.
    /// </summary>
    public static string? Resolve(string name)
    {
        return Resolve(name, TrustedRoots);
    }

    /// <summary>
    ///     The seam the unit tests drive: the same rule against a caller-supplied root list, so a planted binary in a
    ///     test-owned directory can be shown to be rejected without touching the host's real system directories.
    /// </summary>
    public static string? Resolve(string name, IReadOnlyList<string> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roots);

        if (!OperatingSystem.IsLinux())
        {
            // The isolated chain is Linux-only; off Linux there is nothing to resolve and nothing to advertise.
            return null;
        }

        if (name.Contains('/', StringComparison.Ordinal) || name is "." or "..")
        {
            // A helper is named, never pathed: a name carrying a separator could climb out of the trusted roots.
            return null;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, name);
            if (TryResolveTrusted(candidate, MaximumSymbolicLinkDepth) is { } canonical)
            {
                return canonical;
            }
        }

        return null;
    }

    /// <summary>
    ///     <see langword="true" /> when <paramref name="path" /> itself passes the rule — used by the probe to
    ///     re-validate a path it was handed rather than one it resolved.
    /// </summary>
    public static bool IsTrusted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return OperatingSystem.IsLinux() && TryResolveTrusted(path, MaximumSymbolicLinkDepth) is not null;
    }

    /// <summary>
    ///     The full rule: every component of <paramref name="path" /> is trustworthy AND the leaf is a root-owned,
    ///     non-writable, executable regular file. Returns the canonical path, or <see langword="null" /> the moment
    ///     one check fails.
    /// </summary>
    private static string? TryResolveTrusted(string path, int remainingDepth)
    {
        if (TryCanonicalizeTrusted(path, remainingDepth) is not { } canonical)
        {
            return null;
        }

        var leaf = SandboxUnixMetadata.TryRead(canonical, followSymbolicLinks: true);

        return leaf is { IsRegularFile: true, HasAnyExecuteBit: true, UserId: 0, IsGroupOrWorldWritable: false }
            ? canonical
            : null;
    }

    /// <summary>
    ///     Walks <paramref name="path" /> one component at a time, checking each against the ownership rule and
    ///     following any symlink it meets into a recursive validation of its target. Returns the canonical path when
    ///     every component passes, <see langword="null" /> the moment one does not.
    ///     <para>
    ///         It deliberately says nothing about what the leaf IS. A symlink target is validated through this method
    ///         rather than through <see cref="TryResolveTrusted" /> precisely because the target of a usr-merge link is
    ///         a directory, and applying the executable-file rule to it would reject the layout every current
    ///         distribution ships.
    ///     </para>
    /// </summary>
    private static string? TryCanonicalizeTrusted(string path, int remainingDepth)
    {
        if (remainingDepth <= 0 || !Path.IsPathRooted(path))
        {
            return null;
        }

        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var component in components)
        {
            if (component is "." or "..")
            {
                // Canonicalising these away would mean trusting a path that never named the directory it resolves to.
                return null;
            }

            var next = string.Concat(current, "/", component);
            var facts = SandboxUnixMetadata.TryRead(next);
            if (facts is not { } entry || entry.UserId != 0)
            {
                // Absent, unreadable, or owned by anyone but root. A helper the engine's own user could replace is not
                // a helper the engine can build a security boundary out of.
                return null;
            }

            if (entry.IsSymbolicLink)
            {
                // The link's own mode bits are 0777 on Linux and carry no information, so the target decides. The
                // target is validated by the SAME rule, recursively, and becomes the path the walk continues from —
                // which is what makes the returned value canonical.
                var target = ResolveLinkTarget(next);
                if (target is null || TryCanonicalizeTrusted(target, remainingDepth - 1) is not { } canonicalTarget)
                {
                    return null;
                }

                current = canonicalTarget;
                continue;
            }

            if (entry.IsGroupOrWorldWritable)
            {
                // A writable component is a place to swap what comes after it. The sticky-bit exception that makes
                // /tmp acceptable as a JAIL ancestor is deliberately not extended here: no system binary lives under a
                // shared writable directory, so allowing it would only widen the rule for cases that never occur.
                return null;
            }

            current = next;
        }

        return current.Length == 0 ? "/" : current;
    }

    /// <summary>
    ///     Reads one link's immediate target and makes it absolute against the link's own directory. Only one level is
    ///     resolved here; the caller re-enters the full rule on the result, so a chain of links is validated link by
    ///     link rather than jumped over.
    /// </summary>
    private static string? ResolveLinkTarget(string linkPath)
    {
        try
        {
            var target = File.ResolveLinkTarget(linkPath, returnFinalTarget: false)?.ToString();
            if (string.IsNullOrEmpty(target))
            {
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                return target;
            }

            var directory = Path.GetDirectoryName(linkPath);

            return string.IsNullOrEmpty(directory) ? null : Path.GetFullPath(Path.Combine(directory, target));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
