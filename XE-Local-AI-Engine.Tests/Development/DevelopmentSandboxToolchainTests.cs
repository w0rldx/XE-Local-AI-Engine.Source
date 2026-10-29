namespace XE_Local_AI_Engine.Tests.Development;

using System.Runtime.Versioning;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Which host trees Development binds read-only behind the bubblewrap chain, and the <c>PATH</c> that finds them: install
///     prefixes, never a tree exposing a Sandboxed MCP sensitive root (the home, a credential store, the node data directory).
/// </summary>
/// <remarks>
///     The fixture lives under the account's home rather than the system temp, which the chain owns, or the test output directory,
///     which the sensitive roots deny as the engine's install directory: either way every case would pass vacuously. Each case
///     builds the real sensitive-root set over a fake home and data directory inside the fixture.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class DevelopmentSandboxToolchainTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xe-toolchain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_BindsInstallPrefixes_FollowsLinks_AndNeverATreeHoldingAProtectedRoot()
    {
        RequireLinux();

        // A dotnet root holding its executable directly, a versioned Python prefix reached through a `latest` link, a venv
        // whose python links to that prefix, and a ~/.local/bin entry whose parent holds the node data directory.
        var dotnetRoot = Executable(Path.Combine(_root, "dotnet-root"), "dotnet");
        var prefix = Path.Combine(_root, "python", "3.14.8");
        Executable(Path.Combine(prefix, "bin"), "python3.14");
        File.CreateSymbolicLink(Path.Combine(prefix, "bin", "python3"), "python3.14");
        Directory.CreateSymbolicLink(Path.Combine(_root, "python", "latest"), "3.14.8");
        var venv = Directory.CreateDirectory(Path.Combine(_root, "venv", "bin")).Parent!.FullName;
        File.CreateSymbolicLink(Path.Combine(venv, "bin", "python3"), Path.Combine(_root, "python", "latest", "bin", "python3"));
        var home = Path.Combine(_root, "home");
        var localBin = Executable(Path.Combine(home, ".local", "bin"), "git");
        var dataRoot = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "node")).FullName;

        var toolchain = DevelopmentSandboxToolchain.Resolve(["dotnet", "python3", "git"],
            string.Join(Path.PathSeparator, localBin, Path.Combine(venv, "bin"), dotnetRoot, "/usr/bin"),
            Roots(home, dataRoot));

        AssertEx.Equal(string.Join(',', dotnetRoot, prefix, venv), string.Join(',', toolchain.ReadOnlyTrees.Order(StringComparer.Ordinal)));
        AssertEx.False(toolchain.ReadOnlyTrees.Any(tree => home.StartsWith(tree, StringComparison.Ordinal)), "a tree holding the account's home was bound");
        // Host order kept, the unbound ~/.local/bin dropped, the chain's own directories last.
        AssertEx.Equal(string.Join(':', Path.Combine(venv, "bin"), dotnetRoot, "/usr/bin", "/bin"), toolchain.SearchPath);
        AssertEx.Equal(dotnetRoot, toolchain.DotnetRoot, "a dotnet outside /usr is what an apphost behind the chain needs DOTNET_ROOT for.");

        await Task.CompletedTask;
    }

    /// <summary>
    ///     A home reached through a symlink (<c>/home</c> → <c>/var/home</c>) is compared canonically: the trees are canonical, so a
    ///     protected root that is not would never match, and <c>~/.local</c> holding the node data directory was bound.
    /// </summary>
    [Test]
    public async Task Resolve_ComparesAgainstCanonicalRoots_SoASymlinkedHomeCannotExposeTheDataDirectory()
    {
        RequireLinux();

        var realHome = Path.Combine(_root, "var-home", "user");
        _ = Executable(Path.Combine(realHome, ".local", "bin"), "python3");
        _ = Directory.CreateDirectory(Path.Combine(realHome, ".local", "share", "node"));
        var linkedHome = Path.Combine(_root, "home");
        Directory.CreateSymbolicLink(linkedHome, realHome);

        var toolchain = DevelopmentSandboxToolchain.Resolve(["python3"],
            Path.Combine(linkedHome, ".local", "bin"),
            Roots(linkedHome, Path.Combine(linkedHome, ".local", "share", "node")));

        AssertEx.Empty(toolchain.ReadOnlyTrees, "~/.local holds the data directory, through the link or not.");

        await Task.CompletedTask;
    }

    /// <summary>A tree inside a credential store under the home (here <c>~/.config</c>) is refused; one elsewhere under the home is bound.</summary>
    [Test]
    public async Task Resolve_NeverBindsATreeInsideACredentialStore_ButBindsOneElsewhereUnderTheHome()
    {
        RequireLinux();

        var home = Path.Combine(_root, "home");
        var configBin = Executable(Path.Combine(home, ".config", "tool", "bin"), "python3");
        var dotnetRoot = Executable(Path.Combine(home, ".dotnet"), "dotnet");

        var toolchain = DevelopmentSandboxToolchain.Resolve(["python3", "dotnet"],
            string.Join(Path.PathSeparator, configBin, dotnetRoot),
            Roots(home, Path.Combine(_root, "data")));

        AssertEx.Equal(dotnetRoot, string.Join(',', toolchain.ReadOnlyTrees), "~/.config is a credential store; ~/.dotnet is an install.");

        await Task.CompletedTask;
    }

    /// <summary>A venv made through a two-hop alias (<c>latest</c> → <c>3.14</c> → <c>3.14.8</c>): each hop is bound at its own path.</summary>
    /// <remarks>
    ///     The interpreter link and the pyvenv.cfg <c>home</c> both name the alias; without the binds the venv's python dangles behind
    ///     the chain and the bare base interpreter runs instead.
    /// </remarks>
    [Test]
    public async Task Resolve_ShowsEveryAliasHopOfAVenvInterpreter_AtItsOwnPath()
    {
        RequireLinux();

        var installs = Path.Combine(_root, "installs", "python");
        var prefix = Path.Combine(installs, "3.14.8");
        Executable(Path.Combine(prefix, "bin"), "python3.14");
        File.CreateSymbolicLink(Path.Combine(prefix, "bin", "python3"), "python3.14");
        Directory.CreateSymbolicLink(Path.Combine(installs, "3.14"), "./3.14.8");
        Directory.CreateSymbolicLink(Path.Combine(installs, "latest"), "./3.14");
        var venv = Directory.CreateDirectory(Path.Combine(_root, "venv", "bin")).Parent!.FullName;
        File.CreateSymbolicLink(Path.Combine(venv, "bin", "python3"), Path.Combine(installs, "latest", "bin", "python3"));
        await File.WriteAllTextAsync(Path.Combine(venv, "pyvenv.cfg"), $"home = {Path.Combine(installs, "latest", "bin")}\ninclude-system-site-packages = false\n");

        var toolchain = DevelopmentSandboxToolchain.Resolve(["python3"], Path.Combine(venv, "bin"), Roots(Path.Combine(_root, "home"), Path.Combine(_root, "data")));

        AssertEx.Equal(string.Join(',', prefix, venv), string.Join(',', toolchain.ReadOnlyTrees.Order(StringComparer.Ordinal)));
        AssertEx.Equal(string.Join(',', $"{Path.Combine(installs, "3.14")}>{prefix}", $"{Path.Combine(installs, "latest")}>{prefix}"),
            string.Join(',', toolchain.Aliases.Select(static alias => $"{alias.LinkPath}>{alias.Target}").Order(StringComparer.Ordinal)));
        AssertEx.Empty(toolchain.SkippedTrees);

        await Task.CompletedTask;
    }

    /// <summary>A PATH venv under <c>/tmp</c> stays refused, and the refusal is recorded with the executable and the rule, not dropped silently.</summary>
    [Test]
    public async Task Resolve_RecordsATreeItRefuses_WithTheExecutableAndTheReason()
    {
        RequireLinux();

        var temp = Path.Combine("/tmp", "xe-toolchain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var venvBin = Executable(Path.Combine(temp, "venv", "bin"), "python3");

            var toolchain = DevelopmentSandboxToolchain.Resolve(["python3"], venvBin, Roots(Path.Combine(_root, "home"), Path.Combine(_root, "data")));

            AssertEx.Empty(toolchain.ReadOnlyTrees);
            var skipped = AssertEx.NotNull(toolchain.SkippedTrees.SingleOrDefault(entry => string.Equals(entry.Tree, Path.Combine(temp, "venv"), StringComparison.Ordinal)));
            AssertEx.Equal("python3", skipped.Executable);
            AssertEx.Contains(skipped.Reason, "/tmp");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     An alias whose link path is, or contains, something the sandbox already binds would shadow it, so it is dropped; an alias
    ///     beside the bound paths is kept, even one sharing a bound path's name as a prefix.
    /// </summary>
    [Test]
    public async Task WithoutShadowingAliases_DropsAnAliasThatIsOrContainsABoundPath()
    {
        RequireLinux();

        static DevelopmentToolchainAlias Alias(string link) =>
            new()
            {
                LinkPath = link,
                Target = "/opt/python/3.14.8"
            };

        var kept = DevelopmentSandboxToolchain.WithoutShadowingAliases([Alias("/opt/python/latest"), Alias("/home/user/.local"), Alias("/home/user/runtime/nuget"), Alias("/opt/python/latest-other")],
            ["/opt/python/3.14.8", "/home/user/.local/share/mise/installs/python/3.14.8", "/home/user/runtime/nuget"]);

        AssertEx.Equal("/opt/python/latest,/opt/python/latest-other", string.Join(',', kept.Select(static alias => alias.LinkPath)));

        await Task.CompletedTask;
    }

    /// <summary>A refused PATH slot whose executable links into a bound tree is replaced in place by that tree's directory.</summary>
    /// <remarks>
    ///     <c>~/.local</c> holds the data directory, so the slot itself is refused; the substitute keeps its position and so still shadows
    ///     <c>/usr/bin</c> as on the host. The two shapes from review: <c>~/.local/bin/dotnet</c> to <c>~/.dotnet</c>, and uv's <c>python3</c>.
    /// </remarks>
    [Test]
    [Arguments("dotnet")]
    [Arguments("python3")]
    public async Task Resolve_SubstitutesARefusedSlot_WithTheBoundDirectoryItsExecutableLinksInto(string executable)
    {
        // The order is the claim, so a host /usr/bin/<executable> need not exist: wherever it does, the substitute comes first.
        RequireLinux();

        var slot = RefusedSlotFixture(executable, targetNamesTheExecutable: true);
        string home = slot.Home, dataRoot = slot.DataRoot, localBin = slot.LocalBin, binary = slot.Binary;

        var toolchain = DevelopmentSandboxToolchain.Resolve([executable], string.Join(Path.PathSeparator, localBin, "/usr/bin"), Roots(home, dataRoot));

        AssertEx.Equal(string.Join(':', binary, "/usr/bin", "/bin"), toolchain.SearchPath, "the substitute keeps the slot's place ahead of /usr/bin.");
        AssertEx.Equal(DevelopmentSandboxToolchain.Canonicalize(Path.Combine(localBin, executable)),
            FirstMatch(toolchain.SearchPath, executable),
            "looked up by its own name on the sandbox PATH, the executable is the binary the host runs.");
        AssertEx.False(toolchain.ReadOnlyTrees.Any(tree => string.Equals(tree, Path.Combine(home, ".local"), StringComparison.Ordinal)), "~/.local itself is never bound.");
        AssertEx.Empty(toolchain.SkippedTrees, "a substituted executable is reachable, so nothing is reported unreachable for it.");

        await Task.CompletedTask;
    }

    /// <summary>
    ///     A target directory holding only <c>python3.12</c> cannot find <c>python3</c> by name, so the slot is not substituted: the
    ///     directory stays off the PATH and the refusal stays recorded for the unreachable-runner detail.
    /// </summary>
    [Test]
    public async Task Resolve_DoesNotSubstitute_ADirectoryThatCannotFindTheExecutableByName()
    {
        RequireLinux();

        var slot = RefusedSlotFixture("python3", targetNamesTheExecutable: false);
        string home = slot.Home, dataRoot = slot.DataRoot, localBin = slot.LocalBin, binary = slot.Binary;

        var toolchain = DevelopmentSandboxToolchain.Resolve(["python3"], string.Join(Path.PathSeparator, localBin, "/usr/bin"), Roots(home, dataRoot));

        AssertEx.False(toolchain.SearchPath.Split(':').Contains(binary), toolchain.SearchPath);
        AssertEx.Contains(toolchain.SkippedTrees,
            entry => entry.Executable == "python3" && string.Equals(entry.Tree, Path.Combine(home, ".local"), StringComparison.Ordinal),
            "the slot's refusal is still what the unreachable-runner detail names.");

        await Task.CompletedTask;
    }

    // ~/.local holds the data directory, so ~/.local/bin is refused; its executable links into a tree that is bound: ~/.dotnet, or uv's
    // interpreter directory, with or without a `python3` of its own beside `python3.12`.
    private RefusedSlot RefusedSlotFixture(string executable, bool targetNamesTheExecutable)
    {
        var home = Path.Combine(_root, "home");
        var dataRoot = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "node")).FullName;
        var binary = executable == "dotnet"
            ? Executable(Path.Combine(home, ".dotnet"), "dotnet")
            : Executable(Path.Combine(home, ".local", "share", "uv", "python", "cpython-3.12.8-linux-x86_64-gnu", "bin"), "python3.12");
        var real = executable == "dotnet" ? "dotnet" : "python3.12";
        if (executable != "dotnet" && targetNamesTheExecutable)
        {
            File.CreateSymbolicLink(Path.Combine(binary, executable), real);
        }

        var localBin = Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName;
        File.CreateSymbolicLink(Path.Combine(localBin, executable), Path.Combine(binary, real));
        return new RefusedSlot
        {
            Home = home,
            DataRoot = dataRoot,
            LocalBin = localBin,
            Binary = binary
        };
    }

    // What a PATH lookup of `executable` finds first, canonicalized; null when nothing does.
    private static string? FirstMatch(string searchPath, string executable) =>
        searchPath.Split(':')
                  .Select(directory => Path.Combine(directory, executable))
                  .Where(File.Exists)
                  .Select(DevelopmentSandboxToolchain.Canonicalize)
                  .FirstOrDefault();

    /// <summary>A tree inside the node data directory is never bound, whatever it holds.</summary>
    [Test]
    public async Task Resolve_NeverBindsATreeInsideTheDataDirectory()
    {
        RequireLinux();

        var dataRoot = Path.Combine(_root, "data");
        var venvBin = Executable(Path.Combine(dataRoot, "compute-runtime", "venv", "bin"), "python3");

        var toolchain = DevelopmentSandboxToolchain.Resolve(["python3"], venvBin, Roots(Path.Combine(_root, "home"), dataRoot));

        AssertEx.Empty(toolchain.ReadOnlyTrees);

        await Task.CompletedTask;
    }

    /// <summary>
    ///     A tree the launch's descriptor opener would refuse is left out at resolve time, so one bad <c>PATH</c> entry costs that tool
    ///     only, never every command's launch.
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Resolve_SkipsAGroupWritableTree_AndKeepsTheOthers()
    {
        RequireLinux();

        var shared = Path.Combine(_root, "shared-sdk");
        Executable(shared, "dotnet");
        File.SetUnixFileMode(shared, File.GetUnixFileMode(shared) | UnixFileMode.GroupWrite);
        var pythonBin = Executable(Path.Combine(_root, "python", "bin"), "python3");

        var toolchain = DevelopmentSandboxToolchain.Resolve(["dotnet", "python3"], string.Join(Path.PathSeparator, shared, pythonBin), Roots(Path.Combine(_root, "home"), Path.Combine(_root, "data")));

        AssertEx.Equal(Path.Combine(_root, "python"), string.Join(',', toolchain.ReadOnlyTrees));
        AssertEx.False(toolchain.SearchPath.Split(':').Contains(shared), "a dropped tree is not on the sandbox PATH either.");

        await Task.CompletedTask;
    }

    /// <summary>
    ///     A root-owned directory nobody else can write (here <c>/var</c>, as for <c>/opt/dotnet</c>) is a trustworthy read-only bind
    ///     source, as <c>/usr</c> is; a writable bind still requires the engine's own tree.
    /// </summary>
    [Test]
    public async Task ARootOwnedTree_IsAcceptedForAReadOnlyBindOnly()
    {
        RequireLinux();
        const string tree = "/var";
        var facts = SandboxUnixMetadata.ReadOrNull(tree);
        if (facts is not { UserId: 0, IsGroupOrWorldWritable: false })
        {
            throw new SkipTestException("this host's /var is not root-owned and closed to group and world writes.");
        }

        // Running as root (a Linux container), /var IS the engine's own tree, which a writable bind may use: there is no foreign owner here.
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "owner-probe");
        await File.WriteAllTextAsync(probe, string.Empty);
        if (SandboxUnixMetadata.ReadOrNull(probe) is { UserId: 0 })
        {
            throw new SkipTestException("these tests run as root, so a root-owned tree is the engine's own and the writable refusal cannot be shown.");
        }

        SandboxTrustedDescriptorOpener.OpenBindSource(tree, expectFile: false, readOnly: true).Dispose();

        _ = AssertEx.Throws<SandboxIsolationUnavailableException>(() => SandboxTrustedDescriptorOpener.OpenBindSource(tree, expectFile: false, readOnly: false));

        await Task.CompletedTask;
    }

    /// <summary>
    ///     A dotnet the chain shows (a bound tree, or <c>/usr</c>, including <c>/usr/local/share/dotnet</c>, which no apphost default covers)
    ///     becomes the sandbox's <c>DOTNET_ROOT</c>, next to its PATH; an unbound one sets none, and without a boundary nothing is added.
    /// </summary>
    [Test]
    public async Task TheBoundaryEnvironment_CarriesDotnetRoot_ForEveryDotnetTheChainShows()
    {
        RequireLinux();
        AssertEx.Equal("/home/user/.dotnet", DevelopmentSandboxToolchain.DotnetRootFor("/home/user/.dotnet", ["/home/user/.dotnet"]));
        AssertEx.Equal("/usr/lib/dotnet", DevelopmentSandboxToolchain.DotnetRootFor("/usr/lib/dotnet", []));
        AssertEx.Equal("/usr/local/share/dotnet", DevelopmentSandboxToolchain.DotnetRootFor("/usr/local/share/dotnet", []));
        AssertEx.Null(DevelopmentSandboxToolchain.DotnetRootFor("/opt/dotnet", []), "an unbound dotnet is unreachable; naming it as the root would mislead.");

        var bounded = new Dictionary<string, string>(StringComparer.Ordinal);
        DevelopmentWorkspaceTools.AddBoundaryEnvironment(bounded, "/home/user/.dotnet:/usr/bin:/bin", "/home/user/.dotnet");
        AssertEx.Equal("/home/user/.dotnet", bounded["DOTNET_ROOT"]);
        AssertEx.Equal("/home/user/.dotnet:/usr/bin:/bin", bounded["PATH"]);

        var unreachable = new Dictionary<string, string>(StringComparer.Ordinal);
        DevelopmentWorkspaceTools.AddBoundaryEnvironment(unreachable, "/usr/bin:/bin", dotnetRoot: null);
        AssertEx.False(unreachable.ContainsKey("DOTNET_ROOT"), "no dotnet the chain shows, so no root is named.");

        var unbounded = new Dictionary<string, string>(StringComparer.Ordinal);
        DevelopmentWorkspaceTools.AddBoundaryEnvironment(unbounded, searchPath: null, "/home/user/.dotnet");
        AssertEx.Empty(unbounded);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_BindsNothingForASystemToolchain()
    {
        var toolchain = DevelopmentSandboxToolchain.Resolve(["git"], "/usr/bin:/bin", []);

        AssertEx.Empty(toolchain.ReadOnlyTrees);
        AssertEx.Equal("/usr/bin:/bin", toolchain.SearchPath);

        await Task.CompletedTask;
    }

    /// <summary>The create site and the isolation summary read back the declaration the backend was selected under.</summary>
    [Test]
    public async Task DevelopmentFor_ReadsTheHostToolchainDeclarationOnlyFromABackendThatSuppliesTheHostToolchain()
    {
        AssertEx.Equal(SandboxWorkloads.DevelopmentModeHostToolchain, SandboxWorkloads.DevelopmentFor(SandboxProviderCapabilities.SuppliesHostToolchain));
        AssertEx.Equal(SandboxWorkloads.DevelopmentModeImageToolchain, SandboxWorkloads.DevelopmentFor(SandboxProviderCapabilities.SuppliesImageToolchain));
        AssertEx.True(SandboxWorkloads.DevelopmentModeHostToolchain.RequestsFilesystemIsolationWhereAdvertised);
        AssertEx.False(SandboxWorkloads.DevelopmentModeImageToolchain.RequestsFilesystemIsolationWhereAdvertised);

        await Task.CompletedTask;
    }

    private static IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> Roots(string home, string dataRoot) =>
        SandboxedMcpStdioTransport.BuildSensitiveHostRoots(dataRoot, () => home);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("the bubblewrap chain and its descriptor opener are Linux-only.");
        }
    }

    private static string Executable(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        return directory;
    }

    private sealed record RefusedSlot
    {
        public required string Home { get; init; }

        public required string DataRoot { get; init; }

        public required string LocalBin { get; init; }

        public required string Binary { get; init; }
    }
}
