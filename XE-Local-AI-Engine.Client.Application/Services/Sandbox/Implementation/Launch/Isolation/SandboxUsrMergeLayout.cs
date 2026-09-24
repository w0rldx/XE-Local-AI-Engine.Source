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
internal sealed class SandboxUsrMergeEntry
{
    public required string Path { get; init; }

    public required SandboxUsrMergeAction Action { get; init; }

    public required string? Target { get; init; }
}

/// <summary>What the host filesystem says about one legacy root; the seam the layout algorithm is tested through.</summary>
internal sealed class SandboxPathShape
{
    public required bool Exists { get; init; }

    public required bool IsSymbolicLink { get; init; }

    public required bool IsDirectory { get; init; }

    public required string? CanonicalPath { get; init; }
}

/// <summary>
///     Decides how <c>/bin</c>, <c>/sbin</c>, <c>/lib</c>, <c>/lib64</c> and <c>/libx32</c> are reproduced inside the jail, whose only
///     system tree is a single read-only bind of <c>/usr</c>.
/// </summary>
/// <remarks>
///     Not cosmetic: an ELF binary's interpreter is baked in as an absolute path, so a jail omitting <c>/lib64</c> cannot exec anything,
///     and a <c>#!</c> naming <c>/bin/sh</c> fails the same way. A canonical target under <c>/usr</c> becomes a <c>--symlink</c> through
///     the single bind; a real directory outside <c>/usr</c> gets its own <c>--ro-bind</c>; an absent one is omitted. Anything else — a
///     symlink pointing outside <c>/usr</c>, or a non-directory on one of these names — is an UNRECOGNISED layout and the capability is
///     reported unavailable rather than guessed at, a boundary built on an assumption about the host's shape being no boundary.
/// </remarks>
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
                entries.Add(new SandboxUsrMergeEntry
                {
                    Path = root,
                    Action = SandboxUsrMergeAction.Symlink,
                    Target = canonical[1..]
                });
                continue;
            }

            if (shape is { IsSymbolicLink: false, IsDirectory: true })
            {
                entries.Add(new SandboxUsrMergeEntry
                {
                    Path = root,
                    Action = SandboxUsrMergeAction.ReadOnlyBind,
                    Target = null
                });
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
                return new SandboxPathShape
                {
                    Exists = false,
                    IsSymbolicLink = false,
                    IsDirectory = false,
                    CanonicalPath = null
                };
            }

            var finalTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);
            var isSymbolicLink = finalTarget is not null;
            var canonical = finalTarget?.FullName ?? path;

            return new SandboxPathShape
            {
                Exists = true,
                IsSymbolicLink = isSymbolicLink,
                IsDirectory = isDirectory,
                CanonicalPath = canonical
            };
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
