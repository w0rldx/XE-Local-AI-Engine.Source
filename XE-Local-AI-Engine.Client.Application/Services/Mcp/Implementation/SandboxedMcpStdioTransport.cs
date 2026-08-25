namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The <see cref="IClientTransport" /> for a <see cref="McpTrustTier.Sandboxed" /> stdio MCP server: it launches
///     the server INSIDE the substrate and speaks the MCP protocol over the child's standard streams.
///     <para>
///         <b>Why this replaces <c>StdioClientTransport</c> rather than configuring it.</b> That transport owns the
///         launch — it composes and starts the process itself — so there is no seam at which the engine could put the
///         sandbox chain underneath it without reimplementing the launch anyway. What the SDK does offer is
///         <see cref="StreamClientTransport" />, which speaks the same protocol over a pair of streams it did not
///         create. So the substrate starts the process and this hands the SDK the streams: the protocol stays the
///         SDK's, and the launch stays the engine's.
///     </para>
///     <para>
///         <b>Fail-closed.</b> A host whose sandbox backend cannot supply the filesystem boundary is refused here,
///         before a process exists. It does NOT fall back to a host launch — that is exactly the launch this type was
///         written to stop — and the refusal names the tier so the operator can see which decision to revisit.
///     </para>
/// </summary>
internal sealed class SandboxedMcpStdioTransport : IClientTransport
{
    /// <summary>
    ///     The sandbox runtime profile these jails are keyed on. Its own value, not AgentHome's: the attach key hashes
    ///     the profile, so an MCP server can never land in — or tear down — the jail an AgentHome run has staged.
    /// </summary>
    internal const string RuntimeProfile = "mcp-stdio";

    /// <summary>
    ///     The attach-key generation. Bump it to force every MCP jail to be recreated after a change to what this
    ///     transport puts in one. It is deliberately not AgentHome's manifest version: these jails share nothing with
    ///     that layout, and borrowing the number would re-key them for an unrelated reason.
    /// </summary>
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
    private readonly McpServerRecord _record;

    public SandboxedMcpStdioTransport(McpServerRecord record,
        IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        INodeDataDirectory nodeDataDirectory,
        IOptions<ComputeOptions> ceilingDefaults,
        IOptions<LocalContainerOptions> nodeOptions,
        ILoggerFactory loggerFactory)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _ceilingDefaults = (ceilingDefaults ?? throw new ArgumentNullException(nameof(ceilingDefaults))).Value;
        _nodeOptions = (nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions))).Value;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string Name => _record.Name;

    /// <summary>
    ///     Host roots a read-only bind must never cover, because binding one hands the sandboxed server the operator's
    ///     credentials — which is the exact abuse case (threat model AB3) the Sandboxed tier exists to close.
    ///     <para>
    ///         <b>The rule is EQUALS-or-ANCESTOR, not "is under".</b> A tree is refused when it IS one of these roots or
    ///         CONTAINS one; a tree that merely sits beneath one is fine. That asymmetry is the whole design: binding
    ///         the home directory exposes <c>~/.ssh</c>, while binding <c>~/.nvm/versions/node/vX/bin</c> exposes a
    ///         node install and nothing else — and refusing the second would make every <c>npx</c>- or <c>uvx</c>-based
    ///         server unusable at the default tier, which is how a security control gets turned off.
    ///     </para>
    ///     <para>
    ///         Code-owned and engine-composed. It is not configuration: a denylist a registration could edit would be
    ///         no denylist at all.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> BuildSensitiveHostRoots(string nodeDataRoot, Func<string>? resolveHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeDataRoot);

        var home = (resolveHome ?? DefaultHome)();
        if (string.IsNullOrWhiteSpace(home))
        {
            // FAIL CLOSED. Every credential entry on this list is derived from the home directory, so a host that
            // cannot name one (a service account, HOME unset) would silently get a list with the whole
            // credential half missing — the denylist would still be there, still be checked, and still let
            // `WorkingDirectory = /home/someone` through. A tier that cannot enforce its own control refuses.
            throw new SandboxCapabilityNotSupportedException(
                "The Sandboxed MCP trust tier cannot determine this account's home directory, so it cannot tell a server's package tree from the operator's credential stores. "
                + "Run the engine as an account with a home directory (set HOME), or move the server to the Privileged host tier deliberately.");
        }

        var roots = new List<string>(16);

        // The home directory itself, and each credential store under it by name — the second half is not redundant:
        // the equals-or-ancestor rule catches `WorkingDirectory = $HOME` through the first entry, and
        // `WorkingDirectory = ~/.ssh` only through the second.
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
    ///     The read-only host trees a sandboxed server needs to see: where its executable lives, and the working
    ///     directory the operator configured (which is where a stdio server's package files — <c>node_modules</c>, a
    ///     venv, a <c>dist/</c> — actually are).
    ///     <para>
    ///         Engine-derived from the registration and nothing else, and filtered twice. A tree under a mount point
    ///         the isolated chain owns is DROPPED: the chain refuses such a tree (it would be shadowed rather than
    ///         visible), and everything under <c>/usr</c> is already bound read-only by the chain itself, so dropping
    ///         it loses nothing. A tree that equals or contains a <see cref="BuildSensitiveHostRoots" /> entry is
    ///         REFUSED — loudly, naming the path and the tier, because that one is an operator mistake with a real
    ///         consequence and silently dropping it would produce a server that starts and then cannot find its files.
    ///     </para>
    /// </summary>
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

        // The boundary is what the Sandboxed tier IS, so it is checked before anything is created and it does not
        // degrade. The message is engine-authored — it names no host path and no secret — and is surfaced verbatim to
        // the operator rather than redacted, because "this node cannot sandbox" and "your server is broken" need to be
        // tellable apart.
        if (!_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            throw new SandboxCapabilityNotSupportedException(
                $"The MCP server '{_record.Name}' is registered at the Sandboxed trust tier, and this node's '{_provider.ProviderName}' sandbox cannot isolate a process from the host filesystem. "
                + "Install bubblewrap (bwrap) together with the user-namespace support the sandbox containment probe reports as missing, or change this server to the Privileged host tier if it genuinely needs access to this machine.");
        }

        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity), cancellationToken).ConfigureAwait(false);

        ISandboxInteractiveProcess? process = null;
        try
        {
            process = await _provider.StartInteractiveAsync(handle, BuildCommandRequest(), cancellationToken).ConfigureAwait(false);

            // StreamClientTransport's first argument is the stream the client WRITES to reach the server, and the
            // second is the one it READS the server's replies from — so they are the child's stdin and stdout.
            var streamTransport = new StreamClientTransport(process.StandardInput, process.StandardOutput, _loggerFactory);
            var inner = await streamTransport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SandboxedTransport(inner, process, _provider, handle);
        }
        catch
        {
            // Nothing reached the caller, so nothing else will ever tear this down.
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            await KillQuietlyAsync(_provider, handle).ConfigureAwait(false);
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

        // BEFORE the chain-owned check, deliberately: `/` and `/etc` are denied roots that the chain-owned predicate
        // would answer for by dropping them silently, and the operator needs the refusal rather than a server that
        // starts without the tree it asked for.
        if (sensitiveRoots.FirstOrDefault(root => CoversRoot(canonical, root)) is { } covered)
        {
            throw new SandboxCapabilityNotSupportedException(
                $"The MCP server '{serverName}' is registered at the Sandboxed trust tier and would bind '{canonical}' into its sandbox, which contains the sensitive host path '{covered}'. "
                + "Point the server's command or working directory at the directory holding its own files instead — a subdirectory is fine, it is the root itself that cannot be bound.");
        }

        if (!Directory.Exists(canonical)
            || !SandboxIsolatedChain.CanBindReadOnlyTree(canonical)
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
    ///     Normalizes a path and resolves its link chain, so a tree and a denied root are compared as the same
    ///     directory however each was spelled — a symlink to the home directory, a relative segment, a trailing
    ///     separator. Both sides go through this; comparing a resolved tree against an unresolved root is how a
    ///     denylist silently stops matching on a host whose <c>$HOME</c> is itself a link.
    /// </summary>
    private static string? Canonicalize(string path)
    {
        try
        {
            // EVERY ancestor, not just the leaf. Path.GetFullPath is lexical and
            // Directory.ResolveLinkTarget only follows a link that IS the final component, so `link/.ssh/sockets`
            // came back as the literal path and matched no denied root — while bwrap, which binds by descriptor
            // against a kernel that resolves the whole chain, would have mounted ~/.ssh/sockets into the jail.
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

            // A cycle, or a chain deeper than the kernel would follow. Refusing to answer is the fail-closed reading:
            // the caller treats a null tree as "contributes nothing", and a null ROOT would be a hole — which is why
            // BuildSensitiveHostRoots keeps the lexical form when this gives up (see AddRoot).
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
            await provider.KillAsync(handle, CancellationToken.None).ConfigureAwait(false);
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
                // Per SERVER, so two registrations never share a jail and disabling one cannot tear down the other's.
                RuntimeProfile = RuntimeProfile + "-" + _record.Id.ToString("N"),
                ManifestVersion = SandboxGeneration
            },
            RuntimeProfile = RuntimeProfile,
            // Unconditional: ConnectAsync already refused the connection if this provider cannot honour it. Unlike a
            // resource ceiling this is not a preference a provider may quietly drop.
            Isolation = SandboxIsolationMode.Filesystem,
            ReadOnlyTrees = ResolveReadOnlyTrees(_record, ResolveExecutablePath, BuildSensitiveHostRoots(_nodeDataDirectory.Root)),
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
    ///     Finds the HOST path of a configured command so its directory can be bound read-only. A bare name is looked
    ///     up on the engine's own <c>PATH</c>; this is used only to choose a mount, never to compose the launch — the
    ///     child resolves its own executable against the sandbox's <c>PATH</c>.
    /// </summary>
    private static string? ResolveExecutablePath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private SandboxCommandRequest BuildCommandRequest()
    {
        return new SandboxCommandRequest
        {
            ExecutionId = RuntimeProfile + "-" + _record.Id.ToString("N"),
            Executable = _record.Command!,
            Arguments = [.. _record.Arguments],
            // No working directory: the jail IS the working directory. The configured WorkingDirectory is bound
            // READ-ONLY instead (see ResolveReadOnlyTrees) — a third-party server has no reason to write into the tree
            // it was installed from, and the jail is the only writable surface the chain provides.
            Environment = _record.Environment.Count == 0 ? null : _record.Environment
        };
    }

    /// <summary>
    ///     The live transport handed to <c>McpClient</c>: the SDK's stream transport for the protocol, plus ownership
    ///     of the sandbox underneath it. <c>McpClient</c> disposes the transport it was given, which is what makes
    ///     disposing the MCP connection kill the server process and delete its jail with no separate bookkeeping.
    /// </summary>
    private sealed class SandboxedTransport : ITransport
    {
        private readonly SandboxHandle _handle;
        private readonly ITransport _inner;
        private readonly ISandboxInteractiveProcess _process;
        private readonly IAgentSandboxRuntimeProvider _provider;

        public SandboxedTransport(ITransport inner,
            ISandboxInteractiveProcess process,
            IAgentSandboxRuntimeProvider provider,
            SandboxHandle handle)
        {
            _inner = inner;
            _process = process;
            _provider = provider;
            _handle = handle;
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            return _inner.SendMessageAsync(message, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            // Innermost first: stop reading the streams, then kill the process that owns them, then delete the jail.
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
            await KillQuietlyAsync(_provider, _handle).ConfigureAwait(false);
        }
    }
}
