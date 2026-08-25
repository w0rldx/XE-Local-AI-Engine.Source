namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Providers.Abstractions;
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
    /// <summary>Stands in for the node data directory — the engine's database, keys and jails all live under it.</summary>
    private static readonly string NodeDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xe-node-data-fixture");

    /// <summary>Container for every fixture tree these tests bind; never itself a denied root.</summary>
    private static readonly string FixtureRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xe-mcp-test-fixtures");

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

        var transport = new SandboxedMcpStdioTransport(StdioRecord(McpTrustTier.Sandboxed),
            provider,
            IdentityProvider(),
            NodeDataDirectory(),
            NullLoggerFactory.Instance);

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

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, static _ => "/usr/bin/server-binary", SensitiveRoots());

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

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots());

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

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots());

            // A duplicated bind is not merely wasteful: the chain applies mount operations in argument order, and the
            // second bind of the same path would shadow the first.
            AssertEx.Equal(expected: 1, trees.Count);
        }
        finally
        {
            installed.Delete(recursive: true);
        }
    }

    // ---- the sensitive-host-root denylist (threat model AB3) ----

    [Test]
    [Arguments("")]
    [Arguments(".ssh")]
    [Arguments(".gnupg")]
    [Arguments(".aws")]
    [Arguments(".config")]
    [Arguments(".kube")]
    public void ResolveReadOnlyTrees_ForAWorkingDirectoryThatIsACredentialStore_IsRefused(string relative)
    {
        // The Critical this denylist exists for: a registration created through the ordinary settings CRUD, at the
        // DEFAULT tier, with WorkingDirectory pointed at the home directory would have bound ~/.ssh read-only into the
        // jail — the exact abuse case the docs claim the Sandboxed tier closes. The empty argument IS the home root.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var denied = relative.Length == 0 ? home : Path.Combine(home, relative);
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = denied
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(
            () => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
            $"binding '{denied}' would hand the server the operator's credentials.");

        AssertEx.Contains(exception.Message, "Sandboxed");
        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(denied));
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAWorkingDirectoryThatIsTheNodeDataDirectory_IsRefused()
    {
        // The node database, its key material and every sandbox jail live under this root, including the workspace
        // manifests that are deliberately never mounted into any sandbox.
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = NodeDataRoot
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(
            () => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(NodeDataRoot));
    }

    [Test]
    [Arguments("/")]
    [Arguments("/etc")]
    [Arguments("/root")]
    [Arguments("/var")]
    public void ResolveReadOnlyTrees_ForASystemRoot_IsRefused(string absolute)
    {
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = absolute
        };

        _ = AssertEx.Throws<SandboxCapabilityNotSupportedException>(
            () => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
            $"'{absolute}' is never a server's package tree.");
    }

    [Test]
    public void ResolveReadOnlyTrees_ForANonSensitiveSubtreeOfHome_IsAllowed()
    {
        // The other half of the rule, and the half that keeps the control switched on: refusing every subtree of home
        // would make every npx- or uvx-based server unusable at the default tier. A node install exposes a node
        // install; the home directory exposes the operator.
        var nvmBin = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"nvm-{Guid.NewGuid():N}", "versions", "node", "v22.0.0", "bin"));
        try
        {
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "npx",
                WorkingDirectory = null
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                _ => Path.Combine(nvmBin.FullName, "npx"),
                SensitiveRoots());

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(nvmBin.FullName), trees[0]);
        }
        finally
        {
            nvmBin.Parent!.Parent!.Parent!.Parent!.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForASymlinkPointingAtTheHomeDirectory_IsRefused()
    {
        // The comparison happens on resolved paths BOTH sides. Comparing the spelled path would let a one-line
        // `ln -s ~ ~/.xe-link` walk straight past the list.
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("creating a directory symlink is privileged on Windows; the rule is platform-independent.");
        }

        _ = Directory.CreateDirectory(FixtureRoot);
        var link = Path.Combine(FixtureRoot, $"link-to-home-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = link
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(
                () => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

            // The message names the RESOLVED path, which is the one that would have been mounted.
            AssertEx.Contains(exception.Message,
                Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForABareCommandResolvingUnderADeniedRoot_IsRefusedWithTheResolvedPath()
    {
        // The secondary half of the finding: a bare command is looked up on the ENGINE's PATH, so a shim sitting
        // directly in the home directory would have bound home through the command axis rather than the working-
        // directory one. Both axes route through the same gate, which is why one guard covers both.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "npx",
            WorkingDirectory = null
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(
            () => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, _ => Path.Combine(home, "npx"), SensitiveRoots()));

        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(home));
    }

    private static McpClientFactory CreateFactory()
    {
        var options = Options.Create(new McpOptions
        {
            ConnectTimeoutSeconds = 30,
            HttpLoopbackHosts = ["127.0.0.1"]
        });
        return new McpClientFactory(options,
            new FakeSandboxRuntimeProvider(TimeProvider.System),
            IdentityProvider(),
            NodeDataDirectory(),
            NullLoggerFactory.Instance);
    }

    private static IAgentHomeIdentityProvider IdentityProvider()
    {
        return new StubIdentityProvider();
    }

    private static INodeDataDirectory NodeDataDirectory()
    {
        return new FakeNodeDataDirectory(NodeDataRoot);
    }

    /// <summary>The real denylist, so these cases exercise what production composes rather than a stand-in.</summary>
    private static IReadOnlyList<string> SensitiveRoots()
    {
        return SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot);
    }

    /// <summary>
    ///     A directory the isolated chain can actually bind, TWO levels under the home directory rather than one.
    ///     <para>
    ///         The system temp root is <c>/tmp</c>, which the chain owns as a mount point — a tree under it would be
    ///         shadowed rather than visible, so the chain refuses it and <c>ResolveReadOnlyTrees</c> drops it. Home is
    ///         the remaining choice, and these fixtures deliberately sit inside a container directory of their own so
    ///         no fixture is ever a direct child the denylist has to reason about, and so a failed cleanup leaves one
    ///         removable directory rather than scattered dotfiles.
    ///     </para>
    /// </summary>
    private static DirectoryInfo CreateBindableDirectory(string prefix)
    {
        var path = Path.Combine(FixtureRoot, $"{prefix}{Guid.NewGuid():N}");
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
