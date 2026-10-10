namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The <see cref="IClientTransport" /> for a <see cref="McpTrustTier.Sandboxed" /> stdio MCP server: it launches
///     the server INSIDE the substrate and speaks the MCP protocol over the child's standard streams.
/// </summary>
/// <remarks>
///     It replaces <c>StdioClientTransport</c> rather than configuring it, because that transport owns the launch and
///     offers no seam to put the sandbox chain under; <see cref="StreamClientTransport" /> speaks the same protocol
///     over streams it did not create, so the substrate starts the process and the SDK is handed the streams. It is
///     fail-closed: a host whose sandbox backend cannot supply the filesystem boundary is refused before a process
///     exists, never falling back to the host launch this type exists to stop, and the refusal names the tier.
/// </remarks>
internal sealed class SandboxedMcpStdioTransport : IClientTransport
{
    /// <summary>
    ///     The sandbox runtime profile these jails are keyed on. Its own value, not AgentHome's: the attach key hashes
    ///     the profile, so an MCP server can never land in — or tear down — the jail an AgentHome run has staged.
    /// </summary>
    internal const string RuntimeProfile = "mcp-stdio";

    /// <summary>
    ///     The attach-key generation: bump it to force every MCP jail to be recreated after a change to what this
    ///     transport puts in one.
    /// </summary>
    /// <remarks>
    ///     It is deliberately not AgentHome's manifest version: these jails share nothing with that layout, and
    ///     borrowing the number would re-key them for an unrelated reason.
    /// </remarks>
    private const int SandboxGeneration = 1;

    /// <summary>Link hops followed before a path is treated as a cycle. The kernel's own ELOOP limit is 40.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>Credential and configuration stores under the operator's home directory.</summary>
    private static readonly string[] SensitiveHomeSubdirectories =
    [
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        // gcloud, gh, and most CLI credential stores live here.
        ".config",
        ".docker",
        ".kube"
    ];

    /// <summary>System roots that are never a server's package tree, and always somebody's credentials or state.</summary>
    private static readonly string[] SensitiveAbsoluteRoots =
    [
        "/root",
        "/etc",
        "/var",
        "/"
    ];

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ComputeOptions _ceilingDefaults;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly LocalContainerOptions _nodeOptions;
    private readonly IAgentSandboxRuntimeProvider _provider;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly McpServerRecord _record;

    // Names this session's jail and execution: per SERVER for the shared session, per server AND session key for a
    // per-conversation one, so two sessions of one server never share a jail and disposing one cannot kill the other.
    private readonly string _sessionIdentity;

    public SandboxedMcpStdioTransport(McpServerRecord record,
        IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        INodeDataDirectory nodeDataDirectory,
        IOptions<ComputeOptions> ceilingDefaults,
        IOptions<LocalContainerOptions> nodeOptions,
        INodeRuntimeSettings runtimeSettings,
        ILoggerFactory loggerFactory,
        string? sessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _ceilingDefaults = (ceilingDefaults ?? throw new ArgumentNullException(nameof(ceilingDefaults))).Value;
        _nodeOptions = (nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions))).Value;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        // Every transport instance owns a jail nobody else can attach to: the shared session's replacement used to reuse the
        // retiring jail's identity while teardown was still running, and the old kill then hit the replacement (Codex 2026-09-30).
        _sessionIdentity = RuntimeProfile + "-" + record.Id.ToString("N") + (string.IsNullOrEmpty(sessionKey) ? string.Empty : "-" + sessionKey)
                           + "-" + Interlocked.Increment(ref _instanceCounter).ToString(CultureInfo.InvariantCulture);
    }

    private static long _instanceCounter;

    public string Name => _record.Name;

    /// <summary>
    ///     Why the last session never answered the handshake, captured when it was torn down unanswered (the connect timeout), so the
    ///     factory can name the exit code, the sandbox warnings and the stderr tail instead of a bare timeout.
    /// </summary>
    internal McpServerStartupException? UnansweredStartup { get; private set; }

    /// <summary>
    ///     Host roots a read-only bind must never cover, because binding one hands the sandboxed server the operator's
    ///     credentials — the abuse case (threat model AB3) the Sandboxed tier exists to close.
    /// </summary>
    /// <remarks>
    ///     The rule is EQUALS-or-ANCESTOR, not "is under": a tree is refused when it IS one of these roots or CONTAINS
    ///     one, while a tree merely beneath one is fine. Binding the home directory exposes <c>~/.ssh</c>, whereas
    ///     binding <c>~/.nvm/versions/node/vX/bin</c> exposes a node install and nothing else, and refusing that would
    ///     make every <c>npx</c>- or <c>uvx</c>-based server unusable at the default tier, which is how a security
    ///     control gets turned off. The list is code-owned: one a registration could edit would be no denylist at all.
    /// </remarks>
    internal static IReadOnlyList<string> BuildSensitiveHostRoots(string nodeDataRoot, Func<string>? resolveHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeDataRoot);

        var home = (resolveHome ?? DefaultHome)();
        if (string.IsNullOrWhiteSpace(home))
        {
            // FAIL CLOSED: every credential entry on this list derives from the home directory, so a host that cannot name one, such
            // as a service account, would keep a denylist that is checked and still lets the whole credential half through.
            throw new SandboxCapabilityNotSupportedException(
                "The Sandboxed MCP trust tier cannot determine this account's home directory, so it cannot tell a server's package tree from the operator's credential stores. "
                + "Run the engine as an account with a home directory (set HOME), or move the server to the Privileged host tier deliberately.");
        }

        var roots = new List<string>(16);

        // The home directory itself, and each credential store under it by name. The second half is not redundant: the
        // equals-or-ancestor rule catches a working directory of the home itself through the first entry, and one inside .ssh only here.
        AddRoot(roots, home);
        foreach (var relative in SensitiveHomeSubdirectories)
        {
            AddRoot(roots, Path.Combine(home, relative));
        }

        // The engine's own state: the node database, its key material, every sandbox jail, the workspace manifests
        // that are deliberately never mounted into any sandbox.
        AddRoot(roots, nodeDataRoot);

        // The engine's own install directory. A server that could read it could read the assemblies it is being
        // sandboxed BY, plus whatever sits beside them.
        AddRoot(roots, AppContext.BaseDirectory);

        foreach (var absolute in SensitiveAbsoluteRoots)
        {
            AddRoot(roots, absolute);
        }

        return roots;
    }

    /// <summary>
    ///     The read-only host trees a sandboxed server needs to see: where its executable lives, and the configured
    ///     working directory, which is where a stdio server's package files actually are.
    /// </summary>
    /// <remarks>
    ///     They are engine-derived from the registration and nothing else, then filtered twice. A tree under a mount
    ///     point the isolated chain owns is DROPPED, since the chain refuses it as shadowed and everything under
    ///     <c>/usr</c> is already bound read-only there, so nothing is lost. A tree that equals or contains a
    ///     <see cref="BuildSensitiveHostRoots" /> entry is REFUSED loudly, naming the path and the tier, because it is
    ///     an operator mistake whose silent drop would leave a server that starts and cannot find its files.
    /// </remarks>
    internal static IReadOnlyList<string> ResolveReadOnlyTrees(McpServerRecord record,
        Func<string, string?> resolveExecutablePath,
        IReadOnlyList<string> sensitiveRoots)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(resolveExecutablePath);
        ArgumentNullException.ThrowIfNull(sensitiveRoots);

        var trees = new List<string>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(record.Command)
            && resolveExecutablePath(record.Command) is { } executablePath
            && Path.GetDirectoryName(executablePath) is { Length: > 0 } executableDirectory)
        {
            AddBindableTree(trees, executableDirectory, sensitiveRoots, record.Name);
        }

        if (!string.IsNullOrWhiteSpace(record.WorkingDirectory))
        {
            AddBindableTree(trees, record.WorkingDirectory, sensitiveRoots, record.Name);
        }

        return trees;
    }

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_record.Command))
        {
            throw new InvalidOperationException("A stdio MCP server requires a command.");
        }

        // The boundary is what the Sandboxed tier IS, so it is checked before anything is created and never degrades. The message is
        // engine-authored, naming no host path or secret, and reaches the operator verbatim: "cannot sandbox" and "server broken" differ.
        if (!_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            throw new SandboxCapabilityNotSupportedException(
                $"The MCP server '{_record.Name}' is registered at the Sandboxed trust tier, and this node's '{_provider.ProviderName}' sandbox cannot isolate a process from the host filesystem. "
                + SandboxBoundaryRemedy.ForThisHost() + ", or change this server to the Privileged host tier if it genuinely needs access to this machine.");
        }

        // Read per connect: under `high` the declared ceilings are a precondition, so a backend that cannot impose them refuses the server
        // before anything is created, naming the profile, instead of starting it unbounded (ADR 0020).
        SandboxSecurityProfilePolicy.EnsureServed(SandboxWorkloads.McpStdio,
            _provider.Capabilities,
            await _runtimeSettings.GetSandboxSecurityProfileAsync(cancellationToken));

        // In the jail a missing command is just an early exit, so it is checked here against the JAIL's PATH (see JailSearchPath):
        // this node's PATH never reaches the jail, so a command found only there would pass and still not start.
        var jailPath = JailSearchPath(_record);
        if (ResolveExecutablePath(_record.Command, jailPath, ExecutableExtensions(_record, OperatingSystem.IsWindows())) is null)
        {
            throw new FileNotFoundException($"The MCP server '{_record.Name}' command '{_record.Command}' was not found on the sandbox PATH ({jailPath}). "
                                            + "The sandbox does not see this node's PATH: use an absolute path, or set PATH in the server's environment to include the directory holding the command.");
        }

        var identity = await _identityProvider.GetAsync(cancellationToken);
        var handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity), cancellationToken);

        ISandboxInteractiveProcess? process = null;
        try
        {
            process = await _provider.StartInteractiveAsync(handle, BuildCommandRequest(handle), cancellationToken);

            // StreamClientTransport's first argument is the stream the client WRITES to reach the server, and the
            // second is the one it READS the server's replies from — so they are the child's stdin and stdout.
            var streamTransport = new StreamClientTransport(process.StandardInput, process.StandardOutput, _loggerFactory);
            var inner = await streamTransport.ConnectAsync(cancellationToken);
            return new SandboxedTransport(this, inner, process, handle);
        }
        catch
        {
            // Nothing reached the caller, so nothing else will ever tear this down.
            if (process is not null)
            {
                await process.DisposeAsync();
            }

            await KillQuietlyAsync(_provider, handle);
            throw;
        }
    }

    /// <summary>
    ///     The single gate every read-only tree passes through — both the resolved command's directory and the
    ///     configured working directory route here, so the denylist cannot be bypassed by whichever of the two an
    ///     operator sets.
    /// </summary>
    private static void AddBindableTree(List<string> trees, string path, IReadOnlyList<string> sensitiveRoots, string serverName)
    {
        if (Canonicalize(path) is not { } canonical)
        {
            // An unreadable or malformed path contributes no tree. The server will fail to start and say so, which is
            // a better diagnosis than a refusal here that names a path the operator typed.
            return;
        }

        // BEFORE the chain-owned check, deliberately: the root and /etc are denied roots the chain-owned predicate would drop
        // silently, and the operator needs the refusal rather than a server that starts without the tree it asked for.
        if (sensitiveRoots.FirstOrDefault(root => CoversRoot(canonical, root)) is { } covered)
        {
            throw new SandboxCapabilityNotSupportedException(
                $"The MCP server '{serverName}' is registered at the Sandboxed trust tier and would bind '{canonical}' into its sandbox, which contains the sensitive host path '{covered}'. "
                + "Point the server's command or working directory at the directory holding its own files instead — a subdirectory is fine, it is the root itself that cannot be bound.");
        }

        // The chain-owned mount points are a bwrap fact: the Windows AppContainer boundary grants a tree in place and owns none (ADR 0019).
        if (!Directory.Exists(canonical)
            || (!OperatingSystem.IsWindows() && !SandboxIsolatedChain.CanBindReadOnlyTree(canonical))
            || trees.Contains(canonical, StringComparer.Ordinal))
        {
            return;
        }

        trees.Add(canonical);
    }

    /// <summary>
    ///     Whether binding <paramref name="tree" /> would expose <paramref name="root" /> — true when they are the
    ///     same directory, or when <paramref name="root" /> lies beneath <paramref name="tree" />.
    /// </summary>
    private static bool CoversRoot(string tree, string root)
    {
        return string.Equals(tree, root, PathComparison)
               || root.StartsWith(tree.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    ///     Normalizes a path and resolves its link chain, so a tree and a denied root compare as the same directory
    ///     however each was spelled — a symlink to the home directory, a relative segment, a trailing separator.
    /// </summary>
    /// <remarks>
    ///     Both sides go through this: comparing a resolved tree against an unresolved root is how a denylist silently
    ///     stops matching on a host whose <c>$HOME</c> is itself a link.
    /// </remarks>
    private static string? Canonicalize(string path)
    {
        try
        {
            // EVERY ancestor, not just the leaf: Path.GetFullPath is lexical and Directory.ResolveLinkTarget follows only a link that
            // IS the final component, so a path through a linked ancestor matches no denied root while bwrap would still mount it.
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            for (var hop = 0; hop < MaxLinkHops; hop++)
            {
                var resolved = ResolveOneLevel(current);
                if (string.Equals(resolved, current, StringComparison.Ordinal))
                {
                    return current;
                }

                // Restart from the top: a link target can itself sit under links this walk has not seen yet.
                current = resolved;
            }

            // A cycle, or a chain deeper than the kernel would follow. Refusing to answer is the fail-closed reading: a null tree
            // contributes nothing, while a null ROOT would be a hole, so BuildSensitiveHostRoots keeps the lexical form (see AddRoot).
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Walks <paramref name="path" /> from the filesystem root, replacing the first component that is a symlink
    ///     with its target and splicing the remainder on. Returns the input unchanged when no component is a link,
    ///     which is how <see cref="Canonicalize" /> knows it is done.
    /// </summary>
    private static string ResolveOneLevel(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var remainder = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < remainder.Length; index++)
        {
            current = Path.Combine(current, remainder[index]);

            // A missing component cannot be a link, and nothing below it can be either.
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            var target = Directory.Exists(current)
                ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: false)
                : new FileInfo(current).ResolveLinkTarget(returnFinalTarget: false);
            if (target is null)
            {
                continue;
            }

            // A relative target resolves against the LINK's directory, which is what Path.Combine does here.
            var replacement = Path.IsPathRooted(target.FullName)
                ? target.FullName
                : Path.Combine(Path.GetDirectoryName(current) ?? root, target.FullName);
            var tail = string.Join(Path.DirectorySeparatorChar, remainder[(index + 1)..]);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(tail.Length == 0 ? replacement : Path.Combine(replacement, tail)));
        }

        return path;
    }

    /// <summary>
    ///     Adds a denied root in its resolved form, and — when resolution gives up (a link cycle) — in its lexical one
    ///     as well. A root that resolution dropped would be a hole; carrying both spellings can only ever refuse more.
    /// </summary>
    private static void AddRoot(List<string> roots, string path)
    {
        foreach (var candidate in new[]
                 {
                     Canonicalize(path),
                     TryLexical(path)
                 })
        {
            if (candidate is { } value && !roots.Contains(value, StringComparer.Ordinal))
            {
                roots.Add(value);
            }
        }
    }

    private static string? TryLexical(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The account's home directory. A seam so a test can drive the fail-closed path without unsetting HOME.</summary>
    private static string DefaultHome()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static async ValueTask KillQuietlyAsync(IAgentSandboxRuntimeProvider provider, SandboxHandle handle)
    {
        try
        {
            await provider.KillAsync(handle, CancellationToken.None);
        }
        catch (Exception exception) when (exception is SandboxHandleInvalidException or IOException or UnauthorizedAccessException)
        {
            // Best-effort teardown: the jail may already be gone, and a teardown error must not replace the real one.
        }
    }

    // Internal so its ceilings and posture can be asserted without standing up a real isolated chain — the same
    // drift guard every other sandbox create site has.
    internal SandboxCreateRequest BuildCreateRequest(AgentHomeOwnerIdentity identity)
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = identity.OwnerUserId,
                NodeId = identity.NodeId,
                ProviderName = _provider.ProviderName,
                // Per SESSION, so two registrations — or two sessions of one — never share a jail and closing one cannot tear down another.
                RuntimeProfile = _sessionIdentity,
                ManifestVersion = SandboxGeneration
            },
            RuntimeProfile = RuntimeProfile,
            // Unconditional: ConnectAsync already refused the connection if this provider cannot honour it. Unlike a
            // resource ceiling this is not a preference a provider may quietly drop.
            Isolation = SandboxIsolationMode.Filesystem,
            ReadOnlyTrees = ResolveReadOnlyTrees(_record,
                command => ResolveExecutablePath(command, JailSearchPath(_record), ExecutableExtensions(_record, OperatingSystem.IsWindows())),
                BuildSensitiveHostRoots(_nodeDataDirectory.Root)),
            // Stated though the isolated chain's --unshare-net is what enforces it, so the intent is legible at the
            // one place a reader looks for it.
            NetworkPolicy = SandboxNetworkPolicy.None,

            // The host-toolchain ceilings, through the helper every create site shares, so this request cannot
            // disagree with SandboxWorkloads.McpStdio's declaration or with what the isolation panel reports.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.McpStdio,
                _provider.Capabilities,
                _ceilingDefaults,
                _nodeOptions)
        };
    }

    /// <summary>
    ///     The <c>PATH</c> the child resolves a bare command against in the jail: the registration's own when set (applied last, so it
    ///     REPLACES the default), otherwise the platform default.
    /// </summary>
    /// <remarks>
    ///     <see cref="SandboxIsolatedChain.SandboxPath" /> inside the Linux mount namespace; the host <c>PATH</c> the process provider
    ///     passes through under the Windows AppContainer boundary, which has no namespace and so no path of its own.
    /// </remarks>
    internal static string JailSearchPath(McpServerRecord record)
    {
        return JailSearchPath(record, OperatingSystem.IsWindows());
    }

    internal static string JailSearchPath(McpServerRecord record, bool windowsHost)
    {
        if (record.Environment.TryGetValue("PATH", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return windowsHost
            ? Environment.GetEnvironmentVariable("PATH") ?? string.Empty
            : SandboxIsolatedChain.SandboxPath;
    }

    /// <summary>
    ///     The extensions a Windows host tries after an extensionless command's exact name: the registration's <c>PATHEXT</c>, else the
    ///     host's, else the system default. None on Linux, where a command name is looked up exactly.
    /// </summary>
    internal static IReadOnlyList<string> ExecutableExtensions(McpServerRecord record, bool windowsHost)
    {
        if (!windowsHost)
        {
            return [];
        }

        var pathExt = record.Environment.TryGetValue("PATHEXT", out var own) && !string.IsNullOrWhiteSpace(own)
            ? own
            : Environment.GetEnvironmentVariable("PATHEXT");
        return (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    ///     Finds the HOST path of a configured command so its directory can be bound read-only, a bare name being
    ///     looked up on <paramref name="searchPath" />, the jail's <c>PATH</c> (see <see cref="JailSearchPath(McpServerRecord)" />).
    /// </summary>
    /// <remarks>
    ///     It chooses a mount and never composes the launch: the child resolves its own executable against the
    ///     sandbox's <c>PATH</c>. Only the executable's directory is bound, never a symlink target elsewhere or a shebang's
    ///     interpreter, which is why the Sandboxed tier runs self-contained servers only. An extensionless name is tried exactly first,
    ///     then with each of <paramref name="extensions" /> (<see cref="ExecutableExtensions" />), per directory, as Windows does.
    /// </remarks>
    internal static string? ResolveExecutablePath(string command, string searchPath, IReadOnlyList<string>? extensions = null)
    {
        string[] suffixes = extensions is { Count: > 0 } && !Path.HasExtension(command) ? ["", .. extensions] : [""];

        string? Probe(string candidate) =>
            suffixes.Select(suffix => candidate + suffix).FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;

        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Probe(command);
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Probe(Path.Combine(directory, command)) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    internal SandboxCommandRequest BuildCommandRequest(SandboxHandle handle)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = _sessionIdentity,
            Executable = _record.Command!,
            Arguments = [.. _record.Arguments],
            // No working directory: the jail IS the working directory, and the configured one is bound READ-ONLY instead (see
            // ResolveReadOnlyTrees), since a third-party server has no reason to write into the tree it was installed from.
            Environment = BuildEnvironment(handle, _record.Environment)
        };
    }

    /// <summary>
    ///     The server's environment: this sandbox's reported scratch paths (<see cref="SandboxHandle.IsolatedPaths" />), then the
    ///     registration's own variables on top, so an operator can still override <c>HOME</c>.
    /// </summary>
    /// <remarks>
    ///     The scratch overlay applies only to a host-path view (the Windows AppContainer boundary), where nothing else would give the
    ///     server a jail-backed <c>HOME</c>. The chain-served <see cref="SandboxIsolatedPaths.Posix" /> view gets none: bwrap's chain
    ///     already sets those four variables, and repeating them would emit duplicate <c>--setenv</c> arguments.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string>? BuildEnvironment(SandboxHandle handle, IReadOnlyDictionary<string, string> registered)
    {
        if (SandboxIsolatedPaths.Of(handle) is not { } paths || paths == SandboxIsolatedPaths.Posix)
        {
            return registered.Count == 0 ? null : registered;
        }

        var environment = new Dictionary<string, string>(paths.ToEnvironment(), StringComparer.Ordinal);
        foreach (var (name, value) in registered)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    ///     The live transport handed to <c>McpClient</c>: the SDK's stream transport for the protocol, plus ownership
    ///     of the sandbox underneath it.
    /// </summary>
    /// <remarks>
    ///     <c>McpClient</c> disposes the transport it was given, which is what makes disposing the MCP connection kill
    ///     the server process and delete its jail with no separate bookkeeping.
    /// </remarks>
    private sealed class SandboxedTransport : ITransport
    {
        /// <summary>STATUS_DLL_INIT_FAILED as an exit code: MXC documents it as the sandbox blocking Win32k (<c>Ui.Disable</c>, ADR 0019).</summary>
        private const int Win32kDeniedExitCode = unchecked((int)0xC0000142);

        /// <summary>How long a failed send waits for the relay to learn whether the server died on startup.</summary>
        private static readonly TimeSpan RelaySettleWait = TimeSpan.FromSeconds(5);

        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        private readonly SandboxHandle _handle;
        private readonly ITransport _inner;
        private readonly ILogger _logger;
        private readonly SandboxedMcpStdioTransport _owner;
        private readonly ISandboxInteractiveProcess _process;
        private readonly Task _relay;
        private int _disposing;
        private int _received;

        public SandboxedTransport(SandboxedMcpStdioTransport owner,
            ITransport inner,
            ISandboxInteractiveProcess process,
            SandboxHandle handle)
        {
            _owner = owner;
            _logger = owner._loggerFactory.CreateLogger<SandboxedMcpStdioTransport>();
            _inner = inner;
            _process = process;
            _handle = handle;
            _relay = RelayAsync();
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            // A server that died before the SDK registered its initialize request would leave that request pending forever: the
            // SDK fails only requests pending when the channel completes. Refusing the send delivers the startup failure instead.
            ThrowIfStartupFailed();
            try
            {
                await _inner.SendMessageAsync(message, cancellationToken);
            }
            catch (IOException)
            {
                // A write into a dead server's stdin (broken pipe) says nothing useful; its stderr tail does. The relay settles as
                // soon as stdout closes, bounded here in case a descendant keeps it open.
                try
                {
                    await _relay.WaitAsync(RelaySettleWait, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // Not settled: the original failure is all there is to report.
                }

                ThrowIfStartupFailed();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Torn down before the server ever spoke and with no startup failure reported: the connect timed out. Diagnosed BEFORE the
            // teardown, while the exit code and warnings are still readable; the tail wait is bounded (GetStandardErrorTailAsync).
            if (Interlocked.Exchange(ref _disposing, value: 1) == 0 && Volatile.Read(ref _received) == 0 && !_relay.IsCompleted)
            {
                _owner.UnansweredStartup = await DiagnoseStartupFailureAsync("did not complete the MCP handshake");
            }

            // Innermost first: stop reading the streams, then kill the process that owns them, then delete the jail.
            await _inner.DisposeAsync();
            await _relay;
            await _process.DisposeAsync();
            await KillQuietlyAsync(_owner._provider, _handle);
        }

        // Messages go through a channel of this transport's own: a server that closes stdout before any message died on startup, and
        // the SDK fails the pending initialize with the channel's completion, so McpServerStartupException carrying the tail goes there.
        private async Task RelayAsync()
        {
            Exception? error = null;
            try
            {
                await foreach (var message in _inner.MessageReader.ReadAllAsync(CancellationToken.None))
                {
                    _ = Interlocked.Exchange(ref _received, value: 1);
                    await _messages.Writer.WriteAsync(message, CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Relayed as the channel's completion, where the SDK looks for it, rather than lost on a background task.
                error = exception;
            }

            // Only a server that never spoke is a startup failure; a later exit is the session ending, reported as-is. A close this
            // side initiated is diagnosed by DisposeAsync instead, before the process is torn down.
            if (Volatile.Read(ref _received) == 0 && Volatile.Read(ref _disposing) == 0)
            {
                error = await DiagnoseStartupFailureAsync("exited before completing the MCP handshake");
            }

            _ = _messages.Writer.TryComplete(error);
        }

        /// <summary>
        ///     The startup failure the operator sees: the exit code in hex (as NTSTATUS codes read), the sandbox warnings and the redacted
        ///     stderr tail. Logged once; the connection manager scrubs it again.
        /// </summary>
        private async Task<McpServerStartupException> DiagnoseStartupFailureAsync(string what)
        {
            var tail = await _process.GetStandardErrorTailAsync();
            var exitCode = _process.ExitCode;
            var warnings = _process.Warnings;
            var exit = exitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $"0x{unchecked((uint)code):X8}") : null;

            var message = new StringBuilder("The MCP server '").Append(_owner._record.Name).Append("' ").Append(what)
                                                              .Append(exit is null ? "." : " (exit code " + exit + ").");
            if (exitCode == Win32kDeniedExitCode)
            {
                _ = message.Append(" 0xC0000142 is Windows refusing to initialise the process because the sandbox denies the Win32k UI subsystem: "
                                   + "run a native executable, a .NET 10 app or PowerShell 7.7+ (pwsh), not Windows PowerShell 5.1 or a .NET Framework program.");
            }

            if (warnings.Count > 0)
            {
                _ = message.Append(" The sandbox reported: ").Append(string.Join("; ", warnings)).Append('.');
            }

            _ = message.Append(tail is null ? " It wrote nothing to stderr." : " Its stderr ended with:\n" + tail);
            _logger.LogWarning("Sandboxed MCP server {ServerId} {What}; exit code {ExitCode}, {WarningCount} sandbox warning(s), stderr {Stderr}.",
                _owner._record.Id,
                what,
                exit ?? "none (still running)",
                warnings.Count,
                tail is null ? "empty" : "captured");
            return new McpServerStartupException(message.ToString(), tail);
        }

        private void ThrowIfStartupFailed()
        {
            if (_messages.Reader.Completion is { IsFaulted: true } completion
                && completion.Exception?.InnerException is McpServerStartupException startup)
            {
                throw startup;
            }
        }
    }
}
