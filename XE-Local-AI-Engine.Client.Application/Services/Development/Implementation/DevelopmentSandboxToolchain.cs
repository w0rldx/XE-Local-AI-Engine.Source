namespace XE_Local_AI_Engine.Client.Services.Development.Implementation;

using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The host toolchain a Development command needs inside the bubblewrap chain: the install trees to bind read-only and the
///     <c>PATH</c> that finds the same executables the host <c>PATH</c> would.
/// </summary>
/// <remarks>
///     The chain shows only <c>/usr</c> of the host, so a toolchain elsewhere (a per-user SDK, a venv) is invisible unless bound. Each
///     tree is a canonical install prefix that exposes no Sandboxed MCP sensitive root (the home, a credential store, the data
///     directory), compared on canonical paths (a symlinked <c>/home</c> included): <c>~/.local</c> would hand over the node's database. A tree the chain cannot
///     place or the descriptor opener would refuse is left out, so only that tool fails "not found" rather than every launch.
/// </remarks>
internal sealed record DevelopmentSandboxToolchain
{
    private const int MaxLinkHops = 40;

    private const string DotnetExecutable = "dotnet";

    private const string ReservedReason = "it lies under a mount point the sandbox owns (such as /tmp, /etc or /xe-runtime), where it would be shadowed";

    private const string ShadowReason = "it is or contains a path the sandbox already binds, which it would shadow";

    private const string SensitiveReason = "it is, contains or lies inside a protected folder (the home, a credential store, the engine's data or install folder)";

    /// <summary>The chain's own search path, the tail of every resolved <see cref="SearchPath" />.</summary>
    private const string ChainSearchPath = SandboxIsolatedChain.SandboxPath;

    public required IReadOnlyList<string> ReadOnlyTrees { get; init; }

    /// <summary>The host <c>PATH</c> filtered to what the sandbox can see, in host order, then the chain's own directories.</summary>
    public required string SearchPath { get; init; }

    /// <summary>
    ///     Symlinked directories on the way to a bound executable (a version manager's <c>latest</c>, a venv's <c>home</c>) that lie outside
    ///     every bound tree: each is shown inside the chain as a read-only bind of its canonical target at the link's own path.
    /// </summary>
    public IReadOnlyList<DevelopmentToolchainAlias> Aliases { get; init; } = [];

    /// <summary>
    ///     The canonical directory of the resolved <c>dotnet</c> wherever the chain shows it (a bound tree or <c>/usr</c>), else
    ///     <see langword="null" />: the chain clears the host's <c>DOTNET_ROOT</c>, and a framework-dependent apphost needs it.
    /// </summary>
    public string? DotnetRoot { get; init; }

    /// <summary>Every tree a <c>PATH</c> executable needed that the rules refused, with why: the tool may exist yet be unreachable.</summary>
    public IReadOnlyList<DevelopmentSkippedToolchainTree> SkippedTrees { get; init; } = [];

    /// <summary>Resolves <paramref name="executables" /> against <paramref name="hostSearchPath" />.</summary>
    /// <param name="executables">The command names Development runs, as the profile spells them (<c>dotnet</c>, <c>python3</c>, <c>git</c>).</param>
    /// <param name="hostSearchPath">The engine's own <c>PATH</c>.</param>
    /// <param name="sensitiveRoots">What no bound tree may expose: <see cref="SandboxedMcpStdioTransport.BuildSensitiveHostRoots" />.</param>
    public static DevelopmentSandboxToolchain Resolve(IEnumerable<string> executables,
        string? hostSearchPath,
        IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> sensitiveRoots)
    {
        ArgumentNullException.ThrowIfNull(executables);
        ArgumentNullException.ThrowIfNull(sensitiveRoots);

        var directories = (hostSearchPath ?? string.Empty)
                          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                          .Where(Path.IsPathRooted)
                          .ToList();
        var trees = new List<string>();
        var skipped = new List<DevelopmentSkippedToolchainTree>();
        var hops = new List<PendingAlias>();
        var resolved = new List<ResolvedExecutable>();
        foreach (var executable in executables.Distinct(StringComparer.Ordinal))
        {
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, executable);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                // The PATH entry's prefix (a venv, whose python is a link to its base) and the real binary's prefix (that base), then a venv's
                // base interpreter directory (pyvenv.cfg `home`), which the interpreter reads by that spelling to find its standard library.
                var entryRoot = InstallRoot(Canonicalize(directory));
                Skip(skipped, executable, entryRoot, AddTree(trees, entryRoot, sensitiveRoots));
                var binaryDirectory = Path.GetDirectoryName(Canonicalize(candidate))!;
                var binaryRoot = InstallRoot(binaryDirectory);
                Skip(skipped, executable, binaryRoot, AddTree(trees, binaryRoot, sensitiveRoots));
                resolved.Add(new ResolvedExecutable
                {
                    Executable = executable,
                    Directory = directory,
                    EntryRoot = entryRoot,
                    BinaryDirectory = binaryDirectory,
                    Binary = Canonicalize(candidate)
                });
                hops.AddRange(Hops(candidate).Select(hop => new PendingAlias { Executable = executable, Alias = hop }));
                if (VenvHome(entryRoot) is { } home)
                {
                    var homeRoot = InstallRoot(Canonicalize(home));
                    Skip(skipped, executable, homeRoot, AddTree(trees, homeRoot, sensitiveRoots));
                    hops.AddRange(Hops(home).Select(hop => new PendingAlias { Executable = executable, Alias = hop }));
                }

                break;
            }
        }

        var aliases = new List<DevelopmentToolchainAlias>();
        foreach (var pending in hops)
        {
            var link = pending.Alias.LinkPath;
            var target = pending.Alias.Target;
            if (IsSystemTree(link) || trees.Exists(tree => IsSameOrUnder(link, tree)) || aliases.Exists(alias => string.Equals(alias.LinkPath, link, StringComparison.Ordinal)))
            {
                // The link itself is already inside the chain: it resolves wherever its target does.
                continue;
            }

            // A link inside a tree already left out is that tree's problem, recorded once; naming its links again only adds noise.
            if (skipped.Exists(entry => IsSameOrUnder(link, entry.Tree)))
            {
                continue;
            }

            var reason = AliasRefusal(link, target, trees, sensitiveRoots);
            if (reason is null)
            {
                aliases.Add(pending.Alias);
            }

            Skip(skipped, pending.Executable, link, reason);
        }

        bool IsVisible(string directory) => IsSystemTree(directory) || trees.Exists(tree => IsSameOrUnder(directory, tree));

        // Per host PATH slot, in host order: the slot where the chain shows it, else the bound real directory of each executable found
        // there (~/.local/bin/dotnet -> ~/.dotnet), so it shadows later slots as on the host; a substituted executable is not unreachable.
        var visible = new List<string>();
        foreach (var directory in directories)
        {
            var canonical = Canonicalize(directory);
            if (IsVisible(canonical))
            {
                visible.Add(canonical);
                continue;
            }

            foreach (var substitute in resolved.Where(entry => string.Equals(entry.Directory, directory, StringComparison.Ordinal)
                                                               && IsVisible(entry.BinaryDirectory)
                                                               && ResolvesByName(entry)))
            {
                visible.Add(substitute.BinaryDirectory);
                skipped.RemoveAll(entry => string.Equals(entry.Executable, substitute.Executable, StringComparison.Ordinal)
                                           && string.Equals(entry.Tree, substitute.EntryRoot, StringComparison.Ordinal));
            }
        }

        visible.AddRange(ChainSearchPath.Split(':'));

        // Only where the directory finds the executable by its own name as the same binary: a `python3` link into a directory holding only
        // `python3.12` would put a directory on the PATH that resolves nothing, and hide that the tool is unreachable.
        static bool ResolvesByName(ResolvedExecutable entry)
        {
            var named = Path.Combine(entry.BinaryDirectory, entry.Executable);
            return File.Exists(named) && string.Equals(Canonicalize(named), entry.Binary, StringComparison.Ordinal);
        }
        return new DevelopmentSandboxToolchain
        {
            ReadOnlyTrees = trees,
            SearchPath = string.Join(':', visible.Distinct(StringComparer.Ordinal)),
            DotnetRoot = resolved.Find(static entry => string.Equals(entry.Executable, DotnetExecutable, StringComparison.Ordinal)) is { } dotnet
                ? DotnetRootFor(dotnet.BinaryDirectory, trees)
                : null,
            Aliases = aliases,
            SkippedTrees = skipped
        };
    }

    /// <summary>The <c>DOTNET_ROOT</c> for a <c>dotnet</c> whose real binary sits in <paramref name="binaryDirectory" />, or null when the chain cannot see it.</summary>
    /// <remarks>
    ///     Set for a <c>/usr</c> install too: the chain clears <c>DOTNET_ROOT</c>, and an apphost's fallback knows only its default locations,
    ///     which <c>/usr/local/share/dotnet</c> is not. Null only for a dotnet neither bound nor under the chain's own <c>/usr</c>.
    /// </remarks>
    internal static string? DotnetRootFor(string binaryDirectory, IReadOnlyList<string> trees) =>
        IsSystemTree(binaryDirectory) || trees.Any(tree => IsSameOrUnder(binaryDirectory, tree)) ? binaryDirectory : null;

    // Every symlink a lookup of `path` crosses, as the link's own path and its canonical target, in walk order.
    private static List<DevelopmentToolchainAlias> Hops(string path)
    {
        var hops = new List<DevelopmentToolchainAlias>();
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var hop = 0; hop < MaxLinkHops && ResolveFirstLink(current) is { } resolved; hop++)
        {
            hops.Add(new DevelopmentToolchainAlias
            {
                LinkPath = resolved.Link,
                Target = Canonicalize(resolved.Target)
            });
            current = resolved.Path;
        }

        return hops;
    }

    // A venv's pyvenv.cfg `home`: the base interpreter's bin directory, as spelled when the venv was made (often through an alias).
    private static string? VenvHome(string root)
    {
        try
        {
            var config = Path.Combine(root, "pyvenv.cfg");
            return File.Exists(config)
                ? File.ReadLines(config)
                      .Select(static line => line.Split('=', 2))
                      .Where(static pair => pair.Length == 2 && string.Equals(pair[0].Trim(), "home", StringComparison.Ordinal))
                      .Select(static pair => pair[1].Trim())
                      .FirstOrDefault(Path.IsPathRooted)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Why the chain cannot show `link` as a bind of `target`, or null when it can: the same placement, protected-root and trust rules as a
    // tree, applied to where the link sits (its parent directory) and to what it names (a directory inside a bound tree).
    private static string? AliasRefusal(string link,
        string target,
        List<string> trees,
        IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> sensitiveRoots)
    {
        if (!Directory.Exists(target) || !trees.Exists(tree => IsSameOrUnder(target, tree)))
        {
            return $"it is a link to '{target}', which is not a directory inside a bound toolchain tree";
        }

        if (IsSameOrUnder(link, DevelopmentWorkspaceProvider.RuntimeMountRoot))
        {
            return ReservedReason;
        }

        return WithoutShadowingAliases([new DevelopmentToolchainAlias { LinkPath = link, Target = target }], trees).Count == 0
            ? ShadowReason
            : Refusal(link, Path.GetDirectoryName(link) ?? "/", sensitiveRoots);
    }

    /// <summary>The aliases whose link path neither equals nor contains any of <paramref name="targets" />, so none can shadow one.</summary>
    /// <param name="aliases">The candidate aliases.</param>
    /// <param name="targets">In-sandbox paths already bound: the read-only trees and every other mount's target.</param>
    internal static IReadOnlyList<DevelopmentToolchainAlias> WithoutShadowingAliases(IReadOnlyList<DevelopmentToolchainAlias> aliases, IReadOnlyList<string> targets) =>
        [.. aliases.Where(alias => !targets.Any(target => IsSameOrUnder(target, alias.LinkPath)))];

    // The placement and protected-root rules on `path`, then the descriptor opener's trust rule on `opened` (the link's parent for an alias).
    private static string? Refusal(string path, string opened, IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> sensitiveRoots)
    {
        if (!SandboxIsolatedChain.CanBindReadOnlyTree(path))
        {
            return ReservedReason;
        }

        return sensitiveRoots.Any(root => SandboxedMcpStdioTransport.Exposes(path, root)) ? SensitiveReason : OpenRefusal(opened);
    }

    private static void Skip(List<DevelopmentSkippedToolchainTree> skipped, string executable, string tree, string? reason)
    {
        if (reason is not null && !skipped.Exists(entry => string.Equals(entry.Executable, executable, StringComparison.Ordinal) && string.Equals(entry.Tree, tree, StringComparison.Ordinal)))
        {
            skipped.Add(new DevelopmentSkippedToolchainTree
            {
                Executable = executable,
                Tree = tree,
                Reason = reason
            });
        }
    }

    /// <summary>The path with every symbolic link resolved, as <c>realpath</c> would; the input unchanged where a link cannot be read.</summary>
    internal static string Canonicalize(string path)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            if (ResolveFirstLink(current) is not { } resolved)
            {
                return current;
            }

            current = resolved.Path;
        }

        return current;
    }

    // Rewrites the first symlinked component of `path` to its target, or null when no component is a link.
    private static ResolvedLink? ResolveFirstLink(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var prefix = "/" + string.Join('/', segments, 0, index + 1);
            string? target;
            try
            {
                target = new FileInfo(prefix).LinkTarget;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (target is null)
            {
                continue;
            }

            var absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, Path.GetDirectoryName(prefix) ?? "/"));
            var rest = string.Join('/', segments.Skip(index + 1));
            return new ResolvedLink
            {
                Path = Path.TrimEndingDirectorySeparator(rest.Length == 0 ? absolute : Path.Combine(absolute, rest)),
                Link = prefix,
                Target = absolute
            };
        }

        return null;
    }

    // A `bin` directory's parent is the install prefix (lib/, site-packages, pyvenv.cfg); a dotnet root holds its executable directly.
    private static string InstallRoot(string directory) =>
        string.Equals(Path.GetFileName(directory), "bin", StringComparison.Ordinal) ? Path.GetDirectoryName(directory) ?? directory : directory;

    // Binds `tree` unless it is already visible; answers why it was refused, or null.
    private static string? AddTree(List<string> trees, string tree, IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> sensitiveRoots)
    {
        if (IsSystemTree(tree) || trees.Exists(existing => IsSameOrUnder(tree, existing)))
        {
            return null;
        }

        var reason = Refusal(tree, tree, sensitiveRoots);
        if (reason is null)
        {
            trees.RemoveAll(existing => PathContainment.IsUnderRoot(existing, tree));
            trees.Add(tree);
        }

        return reason;
    }

    // The launch opens every tree under this same rule and fails the whole command on the first refusal, so a tree it would refuse
    // (group-writable, owned by another account, a symlinked component) is probed here and left out instead.
    private static string? OpenRefusal(string tree)
    {
        try
        {
            SandboxTrustedDescriptorOpener.OpenBindSource(tree, expectFile: false, readOnly: true).Dispose();
            return null;
        }
        catch (SandboxIsolationUnavailableException exception)
        {
            return exception.Message;
        }
    }

    // Already visible: the chain binds /usr and links or binds the merged-/usr roots.
    private static bool IsSystemTree(string path) =>
        IsSameOrUnder(path, "/usr") || IsSameOrUnder(path, "/bin") || IsSameOrUnder(path, "/sbin");

    private static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || PathContainment.IsUnderRoot(path, root);

    // `Path` with its first link rewritten: `Link` is that link's own path, `Target` what it names.
    private sealed record ResolvedLink
    {
        public required string Path { get; init; }

        public required string Link { get; init; }

        public required string Target { get; init; }
    }

    // An executable as the host PATH resolved it: the slot it was found in, that slot's install root, its real binary and that binary's directory.
    private sealed record ResolvedExecutable
    {
        public required string Executable { get; init; }

        public required string Directory { get; init; }

        public required string EntryRoot { get; init; }

        public required string BinaryDirectory { get; init; }

        public required string Binary { get; init; }
    }

    private sealed record PendingAlias
    {
        public required string Executable { get; init; }

        public required DevelopmentToolchainAlias Alias { get; init; }
    }
}
