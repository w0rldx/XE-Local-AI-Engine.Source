namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     LIVE proof that a <see cref="McpTrustTier.Sandboxed" /> stdio MCP server is really confined: a real bubblewrap
///     chain, a real transient scope, a real long-lived child speaking a real duplex protocol over its standard
///     streams.
///     <para>
///         <b>The server is an in-repo fixture, deliberately.</b> It is a POSIX shell script implementing the three MCP
///         methods a handshake needs, so the suite pulls nothing from a package registry and needs no network — which
///         matters twice over here, because the sandbox under test HAS no network and the test would otherwise be
///         proving that instead of what it claims.
///     </para>
///     <para>
///         <b>Opt-in, and they SKIP rather than pass.</b> Gated on <c>XE_COMPUTE_LIVE=1</c> and on the host actually
///         being able to isolate, for the reason <c>SandboxIsolationLiveTests</c> states: a containment test that goes
///         green on a box which contains nothing reports a guarantee nothing exercised.
///     </para>
/// </summary>
public sealed class SandboxedMcpStdioLiveTests
{
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    [Test]
    public async Task SandboxedServer_CompletesTheMcpHandshake_AndListsItsTools()
    {
        RequireIsolationCapableHost();

        using var fixture = new ShellMcpServerFixture();
        using var provider = CreateProvider();
        var record = fixture.ToRecord(McpTrustTier.Sandboxed);
        var transport = new SandboxedMcpStdioTransport(record,
            provider,
            new StubIdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLoggerFactory.Instance);

        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token);

        var tools = await client.ListToolsAsync(cancellationToken: handshake.Token);

        // The whole path end to end: the substrate launched the process, the SDK spoke MCP over its streams, and the
        // reply came back through setsid → systemd-run → bwrap.
        AssertEx.Equal(expected: 1, tools.Count);
        AssertEx.Equal("probe", tools[0].Name);
    }

    [Test]
    public async Task SandboxedServer_CannotReadAPathOutsideItsJail_AndCannotReachTheNetwork()
    {
        RequireIsolationCapableHost();

        using var fixture = new ShellMcpServerFixture();
        using var provider = CreateProvider();

        // A file the operator can read and the server must not. Under the home directory rather than the system temp
        // root because /tmp is a mount point the isolated chain owns.
        var canary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".xe-mcp-live-canary-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(canary, "host-secret");
        try
        {
            var record = fixture.ToRecord(McpTrustTier.Sandboxed) with
            {
                // Passed through as the server's own environment, which is how the fixture learns what to probe. It is
                // ALSO the assertion that a configured variable survives the chain's --clearenv.
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["XE_PROBE_CANARY"] = canary,
                    ["XE_PROBE_INSIDE"] = fixture.MarkerPath
                }
            };

            var transport = new SandboxedMcpStdioTransport(record,
                provider,
                new StubIdentityProvider(),
                NodeDataDirectory(),
                Options.Create(new ComputeOptions()),
                Options.Create(new LocalContainerOptions()),
                NullLoggerFactory.Instance);

            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client = await McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token);

            var result = await client.CallToolAsync("probe", cancellationToken: handshake.Token);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

            // The environment variable arrived, so the negative results below are about the boundary rather than about
            // the fixture never having been told what to look for.
            AssertEx.Contains(text, "ENV=OK");
            // The two halves together are the claim: the server sees the ONE subtree it was given and nothing above
            // it. Either alone is weak — a jail with no mounts would also report the canary absent.
            AssertEx.Contains(text, "INSIDE=PRESENT");
            AssertEx.Contains(text, "CANARY=ABSENT");
            // The working directory is the jail, which inside the sandbox is /work and nothing else — the configured
            // WorkingDirectory is bound READ-ONLY rather than being made the cwd.
            AssertEx.Contains(text, "CWD=/work");
            // No route out of the empty network namespace — not to the LAN, not to the cloud metadata endpoint.
            AssertEx.Contains(text, "NET=DENIED");
        }
        finally
        {
            File.Delete(canary);
        }
    }

    [Test]
    public async Task SandboxedServer_WithTheHomeDirectoryAsItsWorkingDirectory_IsRefusedBeforeAnyProcessStarts()
    {
        // The Critical: this registration is what a settings CRUD create produces at the DEFAULT tier, and it would
        // have bound the whole home directory — ~/.ssh included — read-only into the jail. Asserted live rather than
        // only in a unit test because the refusal has to happen on the real path, against a host that CAN isolate, so
        // it cannot be mistaken for the fail-closed capability refusal.
        RequireIsolationCapableHost();

        using var fixture = new ShellMcpServerFixture();
        using var provider = CreateProvider();
        var record = fixture.ToRecord(McpTrustTier.Sandboxed) with
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        var transport = new SandboxedMcpStdioTransport(record,
            provider,
            new StubIdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLoggerFactory.Instance);

        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token));

        AssertEx.Contains(exception.Message, "Sandboxed");
        AssertEx.Contains(exception.Message,
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        // Nothing of this server's is left running. A count comparison against a "before" reading would be the
        // stronger claim and is not available: LoadedEngineScopeCount runs the containment probe, which creates a
        // transient scope of its own, so the reading moves for reasons that have nothing to do with this connection.
        // What the refusal being thrown from ResolveReadOnlyTrees does prove — that it happens while the create
        // request is composed, before any sandbox exists — is pinned by the unit tests, which touch no host at all.
        await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() == 0,
            TimeSpan.FromSeconds(20),
            "a refused registration must leave no sandbox scope behind");
    }

    [Test]
    public async Task SandboxedServer_LeavesNoScopeBehind_WhenTheConnectionIsDisposed()
    {
        RequireIsolationCapableHost();

        using var fixture = new ShellMcpServerFixture();
        using var provider = CreateProvider();
        var transport = new SandboxedMcpStdioTransport(fixture.ToRecord(McpTrustTier.Sandboxed),
            provider,
            new StubIdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLoggerFactory.Instance);

        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var client = await McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token);

        await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() > 0, TimeSpan.FromSeconds(20), "the server's scope must appear");

        // An MCP server is long-lived, so the ONLY thing that stops it is the connection being released. If that did
        // not reach the scope's cgroup, every disable/re-enable cycle would leave a third-party process running.
        await client.DisposeAsync();

        await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() == 0,
            TimeSpan.FromSeconds(20),
            "disposing the MCP connection must empty the transient scope, not only close the streams");
    }

    // ---- helpers ----

    private static int LoadedEngineScopeCount()
    {
        var isolation = new HostSandboxContainmentProbe().Containment.FilesystemIsolation;
        return SandboxScopeUnitKiller.TryCreate(isolation)?.ListEngineOwnedUnits().Count ?? 0;
    }

    private static INodeDataDirectory NodeDataDirectory()
    {
        // A throwaway root: what matters here is that the denylist has one to refuse, not what is in it.
        return new FakeNodeDataDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xe-node-data-live-fixture"));
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System);
    }

    private static void RequireIsolationCapableHost()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("the isolated launch chain is Linux-only; the Sandboxed tier is unavailable on other platforms.");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            Skip($"set {EnabledVariable}=1 to allow this suite to create real systemd user scopes and mount namespaces.");
        }

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            Skip("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        var containment = new HostSandboxContainmentProbe().Containment;
        if (!containment.SupportsFilesystemIsolation)
        {
            AssertEx.True(condition: false,
                $"this host has a trusted bwrap, so the filesystem boundary must hold; the probe reported: {containment.FilesystemIsolationUnavailableReason}");
        }
    }

    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner", $"node-{Guid.NewGuid():N}"));
        }
    }

    /// <summary>
    ///     A minimal MCP server in POSIX shell, materialized into a directory the isolated chain can bind read-only.
    ///     <para>
    ///         It answers exactly what a handshake needs — <c>initialize</c>, the <c>notifications/initialized</c> it
    ///         must ignore, <c>tools/list</c> and <c>tools/call</c> — over newline-delimited JSON-RPC on stdin/stdout,
    ///         and echoes back the client's own <c>protocolVersion</c> so it can never be the version negotiation that
    ///         fails. Its one tool reports what it can see from inside, which is the evidence these tests are for.
    ///     </para>
    ///     <para>
    ///         The directory lives under the user's home rather than under the system temp root: <c>/tmp</c> is a mount
    ///         point the chain owns, so a tree beneath it would be shadowed rather than visible and the chain refuses
    ///         it outright.
    ///     </para>
    /// </summary>
    private sealed class ShellMcpServerFixture : IDisposable
    {
        private const string Script = """
                                      #!/bin/sh
                                      # Minimal MCP stdio server. Reads one JSON-RPC message per line.
                                      extract_id() { echo "$1" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p'; }
                                      extract_version() { echo "$1" | sed -n 's/.*"protocolVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p'; }
                                      probe() {
                                        out=""
                                        if [ -n "$XE_PROBE_CANARY" ]; then
                                          if [ -e "$XE_PROBE_CANARY" ]; then out="CANARY=PRESENT"; else out="CANARY=ABSENT"; fi
                                          out="$out ENV=OK"
                                        else
                                          out="ENV=MISSING"
                                        fi
                                        # The POSITIVE control. A jail that could read neither file would report
                                        # CANARY=ABSENT for the wrong reason — because nothing is mounted at all.
                                        if [ -n "$XE_PROBE_INSIDE" ]; then
                                          if [ -e "$XE_PROBE_INSIDE" ]; then out="$out INSIDE=PRESENT"; else out="$out INSIDE=ABSENT"; fi
                                        fi
                                        out="$out CWD=$(pwd)"
                                        # An empty network namespace has no interface but a downed lo, and an empty route
                                        # table. Read them out of the namespace's own procfs rather than trying to connect:
                                        # a failed connect is indistinguishable from nothing listening, which would make
                                        # this control pass on a host with full egress. /dev/tcp is a bash-ism and /bin/sh
                                        # here is not bash, so it would have been a false negative too. sed and grep only:
                                        # awk is NOT in the jail (measured — it returned nothing), and a probe whose tool
                                        # is missing reports the answer the test wants for the wrong reason.
                                        nics=$(sed -n '3,$p' /proc/net/dev 2>/dev/null | sed 's/^[[:space:]]*//' | grep -v '^lo:' | grep -c ':')
                                        routes=$(sed -n '2,$p' /proc/net/route 2>/dev/null | grep -c .)
                                        if [ "$nics" = "0" ] && [ "$routes" = "0" ]; then out="$out NET=DENIED"; else out="$out NET=OPEN nics=$nics routes=$routes"; fi
                                        echo "$out"
                                      }
                                      while IFS= read -r line; do
                                        case "$line" in
                                          *'"method":"initialize"'*|*'"method": "initialize"'*)
                                            id=$(extract_id "$line"); version=$(extract_version "$line")
                                            printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"%s","capabilities":{"tools":{}},"serverInfo":{"name":"xe-live-fixture","version":"1.0.0"}}}\n' "$id" "$version"
                                            ;;
                                          *'"method":"tools/list"'*|*'"method": "tools/list"'*)
                                            id=$(extract_id "$line")
                                            printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"probe","description":"Reports what this server can see.","inputSchema":{"type":"object","properties":{}}}]}}\n' "$id"
                                            ;;
                                          *'"method":"tools/call"'*|*'"method": "tools/call"'*)
                                            id=$(extract_id "$line"); text=$(probe)
                                            printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"%s"}],"isError":false}}\n' "$id" "$text"
                                            ;;
                                          *'"method":"notifications/'*)
                                            ;;
                                          *'"id"'*)
                                            id=$(extract_id "$line")
                                            printf '{"jsonrpc":"2.0","id":%s,"error":{"code":-32601,"message":"Method not found"}}\n' "$id"
                                            ;;
                                        esac
                                      done
                                      """;

        private readonly DirectoryInfo _directory;

        public ShellMcpServerFixture()
        {
            _directory = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".xe-mcp-live-{Guid.NewGuid():N}"));
            ScriptPath = Path.Combine(_directory.FullName, "server.sh");
            MarkerPath = Path.Combine(_directory.FullName, "inside-the-bound-tree.txt");
            File.WriteAllText(MarkerPath, "visible");
            File.WriteAllText(ScriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (OperatingSystem.IsLinux())
            {
                // The chain's descriptor opener refuses a group- or world-writable component, and the script has to be
                // executable by the sandbox's own uid. Guarded rather than unconditional because the mode concept does
                // not exist on Windows — where these tests skip before the fixture is ever constructed.
                File.SetUnixFileMode(ScriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        public string ScriptPath { get; }

        /// <summary>A file INSIDE the bound working directory, so the probe has a positive control.</summary>
        public string MarkerPath { get; }

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort fixture teardown.
            }
        }

        /// <summary>
        ///     The registration a real operator would create for this server: <c>/bin/sh</c> from the chain's read-only
        ///     <c>/usr</c>, the script by absolute path, and the fixture directory as the working directory — which is
        ///     what makes <c>ResolveReadOnlyTrees</c> bind the script's own tree.
        /// </summary>
        public McpServerRecord ToRecord(McpTrustTier tier)
        {
            return new McpServerRecord(Guid.NewGuid(),
                "Live fixture",
                Description: null,
                McpTransportKind.Stdio,
                "/bin/sh",
                [ScriptPath],
                _directory.FullName,
                new Dictionary<string, string>(StringComparer.Ordinal),
                Url: null,
                tier,
                Enabled: true,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);
        }
    }
}
