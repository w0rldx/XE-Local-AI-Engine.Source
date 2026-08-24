namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>How one legacy top-level directory is reproduced inside the jail.</summary>
internal enum SandboxUsrMergeAction
{
    /// <summary>A symlink into the single read-only <c>/usr</c> bind — the usr-merged case.</summary>
    Symlink,

    /// <summary>A read-only bind of a real directory that is NOT part of <c>/usr</c> — the split-usr case.</summary>
    ReadOnlyBind
}

/// <summary>One legacy root and how the chain will reproduce it. <see cref="Target" /> is set only for a symlink.</summary>
internal sealed record SandboxUsrMergeEntry(string Path, SandboxUsrMergeAction Action, string? Target);

/// <summary>What the host filesystem says about one legacy root; the seam the layout algorithm is tested through.</summary>
internal sealed record SandboxPathShape(bool Exists, bool IsSymbolicLink, bool IsDirectory, string? CanonicalPath);

/// <summary>
///     Decides how <c>/bin</c>, <c>/sbin</c>, <c>/lib</c>, <c>/lib64</c> and <c>/libx32</c> are reproduced inside the
///     jail, given that the jail's only system tree is a single read-only bind of <c>/usr</c>.
///     <para>
///         This is not cosmetic. An ELF binary's interpreter is baked into it as an absolute path
///         (<c>/lib64/ld-linux-x86-64.so.2</c>), so a jail that omits <c>/lib64</c> cannot exec anything at all,
///         and a <c>#!</c> line naming <c>/bin/sh</c> fails the same way. The three-way rule below is what makes one
///         chain work on both a usr-merged distribution (every legacy root is a symlink into <c>/usr</c>) and a
///         split-usr one (they are real directories) without pretending to have handled a layout it has not seen.
///     </para>
///     <list type="bullet">
///         <item>Canonical target under <c>/usr</c> → a <c>--symlink</c>, resolved through the single <c>/usr</c> bind.</item>
///         <item>A real directory that is not under <c>/usr</c> → its own <c>--ro-bind</c>.</item>
///         <item>Absent → omitted; a system without <c>/libx32</c> simply has no <c>/libx32</c> inside either.</item>
///         <item>
///             Anything else — a symlink pointing somewhere other than <c>/usr</c>, or a non-directory sitting on one
///             of these names — is an UNRECOGNISED layout, and the capability is reported as unavailable rather than
///             guessed at. Building a boundary out of an assumption about the host's filesystem shape is exactly the
///             kind of quiet approximation this work exists to remove.
///         </item>
///     </list>
/// </summary>
internal static class SandboxUsrMergeLayout
{
    /// <summary>The legacy top-level roots, in the order the chain emits them.</summary>
    public static readonly string[] LegacyRoots =
    [
        "/bin",
        "/sbin",
        "/lib",
        "/lib64",
        "/libx32"
    ];

    /// <summary>
    ///     Applies the rule to every legacy root. <paramref name="inspect" /> is the host probe (or a test double).
    ///     Throws <see cref="SandboxIsolationUnavailableException" /> on an unrecognised layout.
    /// </summary>
    public static IReadOnlyList<SandboxUsrMergeEntry> Resolve(Func<string, SandboxPathShape> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);

        var entries = new List<SandboxUsrMergeEntry>(LegacyRoots.Length);
        foreach (var root in LegacyRoots)
        {
            var shape = inspect(root);
            if (!shape.Exists)
            {
                continue;
            }

            if (shape.CanonicalPath is { } canonical && IsUnderUsr(canonical))
            {
                // bwrap's --symlink takes the target first. A RELATIVE target ("usr/bin") is used deliberately: it
                // resolves inside the jail's own root regardless of what the host's /usr is bound from.
                entries.Add(new SandboxUsrMergeEntry(root, SandboxUsrMergeAction.Symlink, canonical[1..]));
                continue;
            }

            if (shape is { IsSymbolicLink: false, IsDirectory: true })
            {
                entries.Add(new SandboxUsrMergeEntry(root, SandboxUsrMergeAction.ReadOnlyBind, Target: null));
                continue;
            }

            throw new SandboxIsolationUnavailableException(
                $"'{root}' is neither absent, nor a directory, nor a symlink into /usr, so this host's filesystem layout is not one the isolated chain knows how to reproduce");
        }

        return entries;
    }

    /// <summary>The production probe: what the host filesystem actually says about one legacy root.</summary>
    public static SandboxPathShape Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                return new SandboxPathShape(Exists: false, IsSymbolicLink: false, IsDirectory: false, CanonicalPath: null);
            }

            var finalTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);
            var isSymbolicLink = finalTarget is not null;
            var canonical = finalTarget?.FullName ?? path;

            return new SandboxPathShape(Exists: true, isSymbolicLink, isDirectory, canonical);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SandboxIsolationUnavailableException($"'{path}' could not be inspected while resolving the host filesystem layout", exception);
        }
    }

    private static bool IsUnderUsr(string canonical)
    {
        return string.Equals(canonical, "/usr", StringComparison.Ordinal)
               || canonical.StartsWith("/usr/", StringComparison.Ordinal);
    }
}
