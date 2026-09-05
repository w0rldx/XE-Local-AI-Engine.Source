namespace XE_Local_AI_Engine.Tests.Sandbox;

using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for <see cref="TrustedBinaryResolver" />, the rule that decides which <c>bwrap</c> the filesystem
///     boundary is built out of. The interesting assertions are all NEGATIVE: what the resolver must refuse. A
///     resolver that accepts a binary the engine's own user could have written hands the sandboxed workload the
///     program that builds its own jail, so "a fake earlier on the search order is rejected" is the property under
///     test, not "the real one is found".
/// </summary>
public sealed class TrustedBinaryResolverTests
{
    [Test]
    public async Task Resolve_RejectsABinaryInADirectoryTheCurrentUserOwns()
    {
        RequireLinux();
        using var fake = new FakeBinaryDirectory("bwrap");

        // The directory is ours, so anything in it is ours to rewrite. That is the whole disqualification: the check
        // is about who could have PUT the file there, not about what the file currently contains.
        AssertEx.Null(TrustedBinaryResolver.Resolve("bwrap", [fake.Path]));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_PrefersTheRootOwnedRoot_EvenWhenAFakeComesFirstInTheSearchOrder()
    {
        RequireLinux();
        RequireHostBinary("setsid");
        using var fake = new FakeBinaryDirectory("setsid");

        var resolved = AssertEx.NotNull(TrustedBinaryResolver.Resolve("setsid", [fake.Path, "/usr/bin"]));

        AssertEx.False(resolved.StartsWith(fake.Path, StringComparison.Ordinal),
            "a user-owned directory earlier in the search order must never win");
        AssertEx.Equal("/usr/bin/setsid", resolved);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_IgnoresPathEntirely_SoAPlantedBinaryOnPathCannotBeChosen()
    {
        RequireLinux();
        RequireHostBinary("setsid");
        using var fake = new FakeBinaryDirectory("setsid");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{fake.Path}:{originalPath}");

            // The containment probe's other resolver prefers PATH, which is right for an availability question and
            // wrong for a trust one. This resolver must not read it at all.
            AssertEx.Equal("/usr/bin/setsid", TrustedBinaryResolver.Resolve("setsid"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_RejectsASymlinkInAUserOwnedDirectory_EvenWhenItTargetsTheRealBinary()
    {
        RequireLinux();
        RequireHostBinary("setsid");
        using var fake = new FakeBinaryDirectory();
        var link = Path.Combine(fake.Path, "setsid");
        File.CreateSymbolicLink(link, "/usr/bin/setsid");

        // The target is genuine today. The link is not: whoever owns the directory can re-point it between the
        // resolution and the exec, so the link component itself has to be root-owned too.
        AssertEx.Null(TrustedBinaryResolver.Resolve("setsid", [fake.Path]));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_AcceptsAUsrMergedSymlinkedRoot_AndReturnsTheCanonicalTarget()
    {
        RequireLinux();
        RequireHostBinary("setsid");
        if (File.ResolveLinkTarget("/bin", returnFinalTarget: true) is null)
        {
            Skip("this host is not usr-merged, so /bin is not a symlink and there is nothing to canonicalise.");
        }

        // /bin is a root-owned symlink to /usr/bin on a usr-merged distribution. Rejecting it would refuse the layout
        // every current distribution ships; accepting the LINK rather than its target would return a name that can be
        // re-pointed. The canonical target is the only answer that is both.
        AssertEx.Equal("/usr/bin/setsid", TrustedBinaryResolver.Resolve("setsid", ["/bin"]));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_RejectsANameThatCarriesAPathSeparatorOrClimbs()
    {
        RequireLinux();

        AssertEx.Null(TrustedBinaryResolver.Resolve("../../home/attacker/bwrap"));
        AssertEx.Null(TrustedBinaryResolver.Resolve("sub/bwrap"));
        AssertEx.Null(TrustedBinaryResolver.Resolve(".."));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_RejectsANonExecutableFile_EvenUnderARootOwnedRoot()
    {
        RequireLinux();

        // /usr/bin holds plenty of root-owned non-executables in practice (stale data files); resolving one would
        // produce a chain that fails at exec time after the capability was already advertised.
        AssertEx.Null(TrustedBinaryResolver.Resolve("os-release", ["/usr/lib"]));

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsTrusted_RejectsAnExecutableUnderTheCurrentUsersHome()
    {
        RequireLinux();
        using var fake = new FakeBinaryDirectory("bwrap");

        AssertEx.False(TrustedBinaryResolver.IsTrusted(Path.Combine(fake.Path, "bwrap")));

        await Task.CompletedTask;
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("the isolated launch chain and its trust rule are Linux-only.");
        }
    }

    private static void RequireHostBinary(string name)
    {
        if (!File.Exists($"/usr/bin/{name}"))
        {
            Skip($"this host has no /usr/bin/{name} to resolve against.");
        }
    }

    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    /// <summary>A temporary directory owned by the test user, holding an executable that pretends to be a helper.</summary>
    private sealed class FakeBinaryDirectory : IDisposable
    {
        // Under the user's HOME rather than the temp directory, and deliberately. /tmp is world-writable, so a
        // candidate under it is rejected by the writable-component rule before ownership is ever consulted — every
        // assertion here would then pass for a reason that has nothing to do with what it claims to test. A 0755
        // directory the test user owns isolates the ownership rule, which is the one that decides whether a binary
        // the engine's own user could have written can build the sandbox.
        public FakeBinaryDirectory(string? binaryName = null)
        {
            Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".xe-trusted-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (binaryName is not null)
            {
                var file = System.IO.Path.Combine(Path, binaryName);
                File.WriteAllText(file, "#!/bin/sh\nexit 0\n");

                // A real branch, not a vacuous skip: only the Unix mode below is platform-specific, and the fixture
                // is still constructible everywhere.
                if (!OperatingSystem.IsLinux())
                {
                    return;
                }

                File.SetUnixFileMode(file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort test cleanup.
            }
        }
    }
}
