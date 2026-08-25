namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

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

    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAgentSandboxRuntimeProvider _provider;
    private readonly McpServerRecord _record;

    public SandboxedMcpStdioTransport(McpServerRecord record,
        IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        ILoggerFactory loggerFactory)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string Name => _record.Name;

    /// <summary>
    ///     The read-only host trees a sandboxed server needs to see: where its executable lives, and the working
    ///     directory the operator configured (which is where a stdio server's package files — <c>node_modules</c>, a
    ///     venv, a <c>dist/</c> — actually are).
    ///     <para>
    ///         Engine-derived from the registration and nothing else. A tree under a mount point the isolated chain
    ///         owns is dropped rather than passed on: the chain REFUSES such a tree (it would be shadowed rather than
    ///         visible), and everything under <c>/usr</c> — where a system-installed server binary lives — is already
    ///         bound read-only by the chain itself, so dropping it loses nothing.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> ResolveReadOnlyTrees(McpServerRecord record, Func<string, string?> resolveExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(resolveExecutablePath);

        var trees = new List<string>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(record.Command)
            && resolveExecutablePath(record.Command) is { } executablePath
            && Path.GetDirectoryName(executablePath) is { Length: > 0 } executableDirectory)
        {
            AddBindableTree(trees, executableDirectory);
        }

        if (!string.IsNullOrWhiteSpace(record.WorkingDirectory))
        {
            AddBindableTree(trees, record.WorkingDirectory);
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

    private static void AddBindableTree(List<string> trees, string path)
    {
        string canonical;
        try
        {
            // Resolve the link chain: the chain binds the tree at its canonical name, and a symlinked path would
            // otherwise bind a directory whose contents are somewhere the sandbox cannot see.
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Directory.ResolveLinkTarget(canonical, returnFinalTarget: true) is { } target)
            {
                canonical = Path.TrimEndingDirectorySeparator(target.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable or malformed path contributes no tree. The server will fail to start and say so, which is
            // a better diagnosis than a refusal here that names a path the operator typed.
            return;
        }

        if (!Directory.Exists(canonical)
            || !SandboxIsolatedChain.CanBindReadOnlyTree(canonical)
            || trees.Contains(canonical, StringComparer.Ordinal))
        {
            return;
        }

        trees.Add(canonical);
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

    private SandboxCreateRequest BuildCreateRequest(AgentHomeOwnerIdentity identity)
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
            ReadOnlyTrees = ResolveReadOnlyTrees(_record, ResolveExecutablePath),
            // Stated though the isolated chain's --unshare-net is what enforces it, so the intent is legible at the
            // one place a reader looks for it.
            NetworkPolicy = SandboxNetworkPolicy.None
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
