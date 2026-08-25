namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The trust tier's one load-bearing consequence: WHERE a stdio MCP server's process runs. These cases decide it
///     without starting anything — the transport TYPE a record resolves to is the decision, and the fail-closed refusal
///     happens before a sandbox is created — so they hold identically on a host that can isolate and one that cannot.
/// </summary>
public sealed class SandboxedMcpStdioTransportTests
{
    [Test]
    public void BuildTransport_ForASandboxedStdioServer_RoutesThroughTheSubstrate()
    {
        var transport = CreateFactory().BuildTransport(StdioRecord(McpTrustTier.Sandboxed));

        AssertEx.True(transport is SandboxedMcpStdioTransport,
            $"a Sandboxed stdio server must not reach the host launch path; got {transport.GetType().Name}.");
    }

    [Test]
    public void BuildTransport_ForAPrivilegedHostStdioServer_KeepsTheHostLaunch()
    {
        // Unchanged behaviour, now as an explicit per-server grant rather than as the only behaviour there is.
        var transport = CreateFactory().BuildTransport(StdioRecord(McpTrustTier.PrivilegedHost));

        AssertEx.True(transport is StdioClientTransport,
            $"a PrivilegedHost stdio server keeps the host launch; got {transport.GetType().Name}.");
    }

    [Test]
    public void BuildTransport_ForABuiltInTrustedStdioServer_IsRefused()
    {
        // Nothing engine-owned speaks stdio, so a row carrying this tier is a code-versus-database mismatch. Serving
        // it as either of the other two would be picking a privilege level on its behalf.
        var factory = CreateFactory();

        _ = AssertEx.Throws<InvalidOperationException>(() => factory.BuildTransport(StdioRecord(McpTrustTier.BuiltInTrusted)),
            "an engine-owned tier on a stdio registration must be refused, not resolved.");
    }

    [Test]
    public async Task ConnectAsync_OnAHostWithoutAFilesystemBoundary_FailsClosedNamingTheTier()
    {
        // The deterministic backend advertises no filesystem isolation, which is exactly the shape of a Windows node
        // or a Linux node without bubblewrap. The tier must refuse rather than degrade to the host launch it exists to
        // replace, and the message must be actionable — an operator told only "the connection failed" cannot tell this
        // apart from a broken server.
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        AssertEx.False(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation),
            "this case is only meaningful against a backend that cannot isolate.");

        var transport = new SandboxedMcpStdioTransport(StdioRecord(McpTrustTier.Sandboxed), provider, IdentityProvider(), NullLoggerFactory.Instance);

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => transport.ConnectAsync()).ConfigureAwait(false);

        AssertEx.Contains(exception.Message, "Sandboxed");
        AssertEx.Contains(exception.Message, "Privileged host");
        AssertEx.Contains(exception.Message, "bubblewrap");
    }

    [Test]
    public void ResolveReadOnlyTrees_BindsTheWorkingDirectory_AndDropsATreeTheChainAlreadyOwns()
    {
        // The command's own directory is where a stdio server's launcher lives and the working directory is where its
        // package files are, so both are bound read-only. A tree under a mount point the chain owns is DROPPED rather
        // than passed on: the chain refuses such a tree outright, and /usr is already bound read-only by the chain, so
        // dropping it loses nothing while passing it would refuse every system-installed server.
        var packages = CreateBindableDirectory("xe-mcp-trees-");
        try
        {
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "server-binary",
                WorkingDirectory = packages.FullName
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, static _ => "/usr/bin/server-binary");

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(packages.FullName), trees[0]);
        }
        finally
        {
            packages.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForACommandOutsideTheChainsMounts_BindsItsDirectory()
    {
        var installed = CreateBindableDirectory("xe-mcp-install-");
        try
        {
            var executable = Path.Combine(installed.FullName, "server-binary");
            File.WriteAllText(executable, "#!/bin/sh\n");
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = executable,
                WorkingDirectory = null
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path);

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(installed.FullName), trees[0]);
        }
        finally
        {
            installed.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_WhenTheCommandDirectoryIsTheWorkingDirectory_BindsItOnce()
    {
        var installed = CreateBindableDirectory("xe-mcp-same-");
        try
        {
            var executable = Path.Combine(installed.FullName, "server-binary");
            File.WriteAllText(executable, "#!/bin/sh\n");
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = executable,
                WorkingDirectory = installed.FullName
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path);

            // A duplicated bind is not merely wasteful: the chain applies mount operations in argument order, and the
            // second bind of the same path would shadow the first.
            AssertEx.Equal(expected: 1, trees.Count);
        }
        finally
        {
            installed.Delete(recursive: true);
        }
    }

    private static McpClientFactory CreateFactory()
    {
        var options = Options.Create(new McpOptions
        {
            ConnectTimeoutSeconds = 30,
            HttpLoopbackHosts = ["127.0.0.1"]
        });
        return new McpClientFactory(options, new FakeSandboxRuntimeProvider(TimeProvider.System), IdentityProvider(), NullLoggerFactory.Instance);
    }

    private static IAgentHomeIdentityProvider IdentityProvider()
    {
        return new StubIdentityProvider();
    }

    /// <summary>
    ///     A directory the isolated chain can actually bind. The system temp root is <c>/tmp</c>, which the chain owns
    ///     as a mount point — a tree under it would be shadowed rather than visible, so the chain refuses it and
    ///     <c>ResolveReadOnlyTrees</c> drops it. Package trees therefore have to live somewhere else, and so do these
    ///     fixtures.
    /// </summary>
    private static DirectoryInfo CreateBindableDirectory(string prefix)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".{prefix}{Guid.NewGuid():N}");
        return Directory.CreateDirectory(path);
    }

    private static McpServerRecord StdioRecord(McpTrustTier tier)
    {
        return new McpServerRecord(Guid.NewGuid(),
            "Local",
            Description: null,
            McpTransportKind.Stdio,
            "node",
            ["server.js"],
            WorkingDirectory: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            Url: null,
            tier,
            Enabled: true,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }

    /// <summary>
    ///     A hand-written stub rather than a substitute: <c>IAgentHomeIdentityProvider</c> is internal to the
    ///     application assembly, and Castle's dynamic proxy cannot subclass an internal interface from an assembly that
    ///     is not strong-named and does not expose itself to <c>DynamicProxyGenAssembly2</c>.
    /// </summary>
    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner", "node"));
        }
    }
}
