namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NSubstitute;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The trust tier's one load-bearing consequence: WHERE a stdio MCP server's process runs. These cases decide it
///     without starting anything — the transport TYPE a record resolves to is the decision, and the fail-closed refusal
///     happens before a sandbox is created — so they hold identically on a host that can isolate and one that cannot.
/// </summary>
[TUnit.Core.Category(TestCategories.Unit)]
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
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            new StubNodeRuntimeSettings().Build(),
            NullLoggerFactory.Instance);

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => transport.ConnectAsync());

        AssertEx.Contains(exception.Message, "Sandboxed");
        AssertEx.Contains(exception.Message, "Privileged host");
        AssertEx.Contains(exception.Message, "bubblewrap");
    }

    /// <summary>
    ///     The create request carries exactly what the shared derivation yields for this role's declaration, so the
    ///     site cannot drift from the constant the isolation panel reports. A Sandboxed MCP server runs an
    ///     operator-installed program, which is why it is bounded at all — and on the HOST-TOOLCHAIN profile, because
    ///     run_python's script-sized ceiling would strangle a language server on its first index.
    /// </summary>
    [Test]
    public void BuildCreateRequest_CarriesTheHostToolchainCeilingsAndTheIsolatedPosture()
    {
        var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
        provider.ProviderName.Returns("process");
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsHostFilesystemBoundary
                                      | SandboxProviderCapabilities.SupportsFilesystemIsolation
                                      | SandboxProviderCapabilities.SupportsNetworkPolicy
                                      | SandboxProviderCapabilities.SupportsResourceLimits);

        var transport = new SandboxedMcpStdioTransport(StdioRecord(McpTrustTier.Sandboxed),
            provider,
            IdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            new StubNodeRuntimeSettings().Build(),
            NullLoggerFactory.Instance);

        var request = transport.BuildCreateRequest(new AgentHomeOwnerIdentity
        {
            OwnerUserId = "owner",
            NodeId = "node"
        });

        AssertEx.Equal(SandboxIsolationMode.Filesystem, request.Isolation);
        AssertEx.Equal(SandboxNetworkPolicy.None, request.NetworkPolicy);
        AssertEx.Equal(SandboxCeilingProfile.HostToolchain, SandboxWorkloads.McpStdio.Ceilings);
        AssertEx.Equal(SandboxResourceCeilings.Resolve(SandboxWorkloads.McpStdio,
                provider.Capabilities,
                new ComputeOptions(),
                new LocalContainerOptions()),
            request.ResourceLimits);
    }

    /// <summary>
    ///     Under <c>high</c> the tier's declared ceilings are a precondition: a backend that isolates but cannot impose ceilings is refused
    ///     before anything is created, naming the profile (ADR 0020).
    /// </summary>
    [Test]
    public async Task ConnectAsync_UnderTheHighProfile_OnABackendWithoutCeilings_RefusesNamingTheProfile()
    {
        var provider = IsolatingProviderWithoutCeilings();
        var transport = MissingCommandTransport(provider, SandboxSecurityProfile.High);

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => transport.ConnectAsync());

        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.ProfileOptionKey);
        AssertEx.Contains(exception.Message, SandboxSecurityProfilePolicy.Remedy);
        await provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
    }

    // The control: the same backend under `low` passes the profile check and reaches the next one, the PATH lookup of a command that
    // cannot exist, which fails without creating anything. So the refusal above is the profile's and nothing else's.
    [Test]
    public async Task ConnectAsync_UnderTheLowProfile_OnABackendWithoutCeilings_IsNotRefusedByTheProfile()
    {
        var provider = IsolatingProviderWithoutCeilings();
        var transport = MissingCommandTransport(provider, SandboxSecurityProfile.Low);

        _ = await AssertEx.ThrowsAsync<FileNotFoundException>(() => transport.ConnectAsync());
        await provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
    }

    private static IAgentSandboxRuntimeProvider IsolatingProviderWithoutCeilings()
    {
        var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
        provider.ProviderName.Returns("process");
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsHostFilesystemBoundary
                                      | SandboxProviderCapabilities.SupportsFilesystemIsolation
                                      | SandboxProviderCapabilities.SupportsNetworkPolicy);
        return provider;
    }

    private static SandboxedMcpStdioTransport MissingCommandTransport(IAgentSandboxRuntimeProvider provider, SandboxSecurityProfile profile)
    {
        return new SandboxedMcpStdioTransport(StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/nonexistent/xe-mcp-missing-server"
            },
            provider,
            IdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            StubNodeRuntimeSettings.Create().WithSandboxSecurityProfile(profile).Build(),
            NullLoggerFactory.Instance);
    }

    [Test]
    public async Task ConnectAsync_CommandMissingOnTheHost_ThrowsFileNotFoundBeforeAnySandboxExists()
    {
        // Inside the jail a missing command is only an early exit, which the connection manager could not tell from any other, so
        // the transport checks up front and the manager reports ServerNotFound.
        var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsFilesystemIsolation);
        var transport = new SandboxedMcpStdioTransport(StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/nonexistent/xe-mcp-missing-server"
            },
            provider,
            IdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            new StubNodeRuntimeSettings().Build(),
            NullLoggerFactory.Instance);

        _ = await AssertEx.ThrowsAsync<FileNotFoundException>(() => transport.ConnectAsync());

        _ = await provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
    }

    [Test]
    public async Task ConnectAsync_BareCommandNotOnTheJailPath_ThrowsFileNotFoundNamingTheJailPath_BeforeAnySandboxExists()
    {
        // The live-round defect: a registration setting PATH skipped the pre-check entirely, and one without it was checked against
        // THIS node's PATH, which the jail never sees, so an npm-installed server passed and then died silently inside the jail.
        var provider = IsolatingProvider();
        var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = $"xe-mcp-not-on-jail-path-{Guid.NewGuid():N}",
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PATH"] = Path.Combine(FixtureRoot, "no-such-dir")
                }
            },
            provider);

        var exception = await AssertEx.ThrowsAsync<FileNotFoundException>(() => transport.ConnectAsync());

        AssertEx.Contains(exception.Message, Path.Combine(FixtureRoot, "no-such-dir"));
        _ = await provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
    }

    [Test]
    public async Task ConnectAsync_BareCommandWithoutARegistrationPath_IsCheckedAgainstTheDefaultJailPath()
    {
        var provider = IsolatingProvider();
        var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = $"xe-mcp-not-on-jail-path-{Guid.NewGuid():N}"
            },
            provider);

        var exception = await AssertEx.ThrowsAsync<FileNotFoundException>(() => transport.ConnectAsync());

        AssertEx.Contains(exception.Message, "/usr/bin:/bin");
        _ = await provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
    }

    [Test]
    public void ResolveExecutablePath_LooksABareNameUpOnTheGivenSearchPath_NotOnTheEnginesPath()
    {
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");

            AssertEx.Equal(server, SandboxedMcpStdioTransport.ResolveExecutablePath("xe-server", directory.FullName));
            AssertEx.Null(SandboxedMcpStdioTransport.ResolveExecutablePath("xe-server", Path.Combine(FixtureRoot, "no-such-dir")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ConnectAsync_BareCommandOnTheRegistrationPath_ProceedsToTheSandbox()
    {
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            _ = CreateExecutable(directory, "xe-server");
            await using var process = new ScriptedInteractiveProcess(stderrTail: null);
            var provider = IsolatingProvider();
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ISandboxInteractiveProcess>(process));
            var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
                {
                    Command = "xe-server",
                    Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["PATH"] = directory.FullName
                    }
                },
                provider);

            await using var connected = await transport.ConnectAsync();

            _ = await provider.ReceivedWithAnyArgs(1).CreateOrAttachAsync(default!, default);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ConnectAsync_AbsoluteCommand_IsNotAffectedByTheRegistrationPath()
    {
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");
            await using var process = new ScriptedInteractiveProcess(stderrTail: null);
            var provider = IsolatingProvider();
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ISandboxInteractiveProcess>(process));
            var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
                {
                    Command = server,
                    Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["PATH"] = Path.Combine(FixtureRoot, "no-such-dir")
                    }
                },
                provider);

            await using var connected = await transport.ConnectAsync();

            _ = await provider.ReceivedWithAnyArgs(1).CreateOrAttachAsync(default!, default);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ConnectAsync_TwoSessionsOfOneServer_RunInTheirOwnJails_AndClosingOneLeavesTheOtherAlive()
    {
        // The blocker: every session of a server derived the same attach key and execution id, so a per-conversation session attached
        // the shared session's jail, the provider refused the duplicate execution id, and the failure path killed the shared jail.
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = CreateExecutable(directory, "xe-server")
            };
            var inFlight = new HashSet<(string SandboxId, string ExecutionId)>();
            var killed = new List<string>();
            var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
            provider.ProviderName.Returns("process");
            provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsFilesystemIsolation);
            // One jail per attach key, as the real provider attaches an existing jail by key.
            provider.CreateOrAttachAsync(Arg.Any<SandboxCreateRequest>(), Arg.Any<CancellationToken>())
                    .Returns(call => Task.FromResult(new SandboxHandle
                    {
                        ProviderName = "process",
                        SandboxId = call.Arg<SandboxCreateRequest>().AttachKey.RuntimeProfile,
                        AttachKey = call.Arg<SandboxCreateRequest>().AttachKey,
                        CreatedAt = DateTimeOffset.UnixEpoch,
                        ManifestVersion = 1
                    }));
            // The real provider's rule: one in-flight execution per id per jail.
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(call => inFlight.Add((call.Arg<SandboxHandle>().SandboxId, call.Arg<SandboxCommandRequest>().ExecutionId))
                        ? Task.FromResult<ISandboxInteractiveProcess>(new ScriptedInteractiveProcess(stderrTail: null))
                        : throw new InvalidOperationException("Execution id already in flight for this sandbox."));
            provider.KillAsync(Arg.Any<SandboxHandle>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        killed.Add(call.Arg<SandboxHandle>().SandboxId);
                        return Task.CompletedTask;
                    });

            await using var shared = await CreateTransport(record, provider).ConnectAsync();
            var conversation = await new SandboxedMcpStdioTransport(record,
                provider,
                IdentityProvider(),
                NodeDataDirectory(),
                Options.Create(new ComputeOptions()),
                Options.Create(new LocalContainerOptions()),
                new StubNodeRuntimeSettings().Build(),
                NullLoggerFactory.Instance,
                sessionKey: Guid.NewGuid().ToString("N")).ConnectAsync();

            AssertEx.Equal(expected: 2, inFlight.Count);
            AssertEx.Equal(expected: 2, inFlight.Select(static entry => entry.SandboxId).Distinct(StringComparer.Ordinal).Count());
            AssertEx.Equal(expected: 2, inFlight.Select(static entry => entry.ExecutionId).Distinct(StringComparer.Ordinal).Count());
            AssertEx.Equal(expected: 0, killed.Count, "starting the second session must not tear down the first");

            await conversation.DisposeAsync();

            var sharedPrefix = SandboxedMcpStdioTransport.RuntimeProfile + "-" + record.Id.ToString("N") + "-";
            // The shared session carries no session key, so its identity is the shorter "<profile>-<record>-<instance>" form.
            var sharedJail = inFlight.Select(static entry => entry.SandboxId).Where(id => id.StartsWith(sharedPrefix, StringComparison.Ordinal)).MinBy(static id => id.Length)!;
            AssertEx.Equal(expected: 1, killed.Count);
            AssertEx.False(killed.Contains(sharedJail, StringComparer.Ordinal), "closing a conversation session must leave the shared session's jail alive");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Handshake_WhenTheServerExitsBeforeSpeaking_ThrowsStartupExceptionCarryingTheStderrTail()
    {
        // The other half of the defect: stderr was discarded, so a server that died on startup surfaced as a generic Transport
        // failure. The SDK fails the pending initialize with the transport channel's completion, which is where the tail goes.
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");
            await using var process = new ScriptedInteractiveProcess("Error: Cannot find module 'server.js'");
            var provider = IsolatingProvider();
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ISandboxInteractiveProcess>(process));
            var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
                {
                    Command = server
                },
                provider);

            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var exception = await AssertEx.ThrowsAsync<McpServerStartupException>(() => McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token));

            AssertEx.Equal("Error: Cannot find module 'server.js'", exception.StderrTail);
            AssertEx.Contains(exception.Message, "Cannot find module");
            await provider.ReceivedWithAnyArgs(1).KillAsync(default!, default);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task PrivilegedHostLaunch_OfAMissingCommand_FailsAsAnIOExceptionWrappingTheWin32Error()
    {
        // Pins the SDK shape McpServerConnectionManager classifies as ServerNotFound: StdioClientTransport wraps a failed
        // Process.Start in IOException("Failed to connect transport."). A new SDK that changes it turns this red first.
        var record = StdioRecord(McpTrustTier.PrivilegedHost) with
        {
            Command = "/nonexistent/xe-mcp-missing-server"
        };

        var exception = await AssertEx.ThrowsAsync<IOException>(() => CreateFactory().CreateAsync(record, sessionKey: null, CancellationToken.None));

        AssertEx.True(exception.InnerException is Win32Exception, $"expected a Win32Exception inner exception, got {exception.InnerException?.GetType().Name ?? "none"}");
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

    // Sensitive-host-root denylist (threat model AB3).
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

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
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

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

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

        _ = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
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

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

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

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, _ => Path.Combine(home, "npx"), SensitiveRoots()));

        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(home));
    }

    [Test]
    public void ResolveReadOnlyTrees_WhenASymlinkedAncestorResolvesOntoADeniedRoot_IsRefused()
    {
        // THE CRITICAL, and it needs a NON-LEAF link to show: Path.GetFullPath is lexical and ResolveLinkTarget
        // follows only a final component, so `link/home` came back as the literal path and matched no denied root —
        // while bwrap binds by descriptor against a kernel that resolves the whole chain, and would have mounted the
        // directory holding the credential store. The leaf-link case was already covered; this is the one that got
        // through.
        RequireSymlinks();
        var estate = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"estate-{Guid.NewGuid():N}"));
        try
        {
            var account = Directory.CreateDirectory(Path.Combine(estate.FullName, "home"));
            _ = Directory.CreateDirectory(Path.Combine(account.FullName, ".ssh"));
            var link = Path.Combine(FixtureRoot, $"link-{Guid.NewGuid():N}");
            Directory.CreateSymbolicLink(link, estate.FullName);

            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = Path.Combine(link, "home")
            };

            // This fixture's own tree stands in for a home directory: what is under test is ANCESTOR resolution, not
            // which roots are on the list.
            var roots = new[]
            {
                Path.Combine(account.FullName, ".ssh")
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, roots),
                "a tree reached through a symlinked ANCESTOR must resolve to its real location before the comparison.");

            AssertEx.Contains(exception.Message, account.FullName);
        }
        finally
        {
            estate.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForASymlinkedAncestorPointingIntoAnAllowedSubtree_IsBoundAtItsResolvedPath()
    {
        // The other direction: resolution must not turn into a blanket refusal. `~/tools -> ~/.nvm/versions/x` is a
        // perfectly ordinary setup, and it has to bind — at the RESOLVED path, because that is what the kernel will
        // mount and what the chain's descriptor opener will validate.
        RequireSymlinks();
        var nvm = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"nvm-{Guid.NewGuid():N}", "versions", "v22"));
        try
        {
            var link = Path.Combine(FixtureRoot, $"tools-{Guid.NewGuid():N}");
            Directory.CreateSymbolicLink(link, nvm.FullName);

            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = link
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots());

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(nvm.FullName), trees[0]);
        }
        finally
        {
            nvm.Parent!.Parent!.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments("{0}/./sub/..")]
    [Arguments("{0}/sub/../")]
    [Arguments("{0}//sub//..//")]
    public void ResolveReadOnlyTrees_NormalizesRelativeSegmentsAndSeparators(string template)
    {
        // Each of these names the fixture directory itself. If normalization differed between the tree and the denied
        // roots, a spelling would be all it took to walk past the list.
        var tree = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"norm-{Guid.NewGuid():N}"));
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(tree.FullName, "sub"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = string.Format(CultureInfo.InvariantCulture, template, tree.FullName)
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots());

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(tree.FullName), trees[0]);
        }
        finally
        {
            tree.Delete(recursive: true);
        }
    }

    [Test]
    public void BuildSensitiveHostRoots_WhenTheHomeDirectoryCannotBeDetermined_FailsClosed()
    {
        // Every credential entry is derived from home, so a host that cannot name one would get a list with the whole
        // credential half missing — still present, still checked, and still letting /home/someone through. A tier that
        // cannot enforce its own control refuses instead.
        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, static () => string.Empty));

        AssertEx.Contains(exception.Message, "home directory");
        AssertEx.Contains(exception.Message, "Privileged host");
    }

    [Test]
    public void BuildSensitiveHostRoots_WithAHomeDirectory_CarriesEveryCredentialStore()
    {
        var roots = SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, static () => "/srv/accounts/xe");

        foreach (var expected in new[]
                 {
                     "/srv/accounts/xe",
                     "/srv/accounts/xe/.ssh",
                     "/srv/accounts/xe/.aws",
                     "/srv/accounts/xe/.kube"
                 })
        {
            AssertEx.Contains(roots, expected);
        }
    }

    private static void RequireSymlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("creating a directory symlink is privileged on Windows; the rule is platform-independent.");
        }

        _ = Directory.CreateDirectory(FixtureRoot);
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
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            new StubNodeRuntimeSettings().Build(),
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

    private static IAgentSandboxRuntimeProvider IsolatingProvider()
    {
        var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
        provider.ProviderName.Returns("process");
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsFilesystemIsolation);
        provider.CreateOrAttachAsync(Arg.Any<SandboxCreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new SandboxHandle
                {
                    ProviderName = "process",
                    SandboxId = "sandbox",
                    AttachKey = call.Arg<SandboxCreateRequest>().AttachKey,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    ManifestVersion = 1
                }));
        return provider;
    }

    private static SandboxedMcpStdioTransport CreateTransport(McpServerRecord record, IAgentSandboxRuntimeProvider provider)
    {
        return new SandboxedMcpStdioTransport(record,
            provider,
            IdentityProvider(),
            NodeDataDirectory(),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            new StubNodeRuntimeSettings().Build(),
            NullLoggerFactory.Instance);
    }

    private static string CreateExecutable(DirectoryInfo directory, string name)
    {
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }

    /// <summary>A server that has already exited: its stdout is at end of stream and its stderr tail is scripted.</summary>
    private sealed class ScriptedInteractiveProcess : ISandboxInteractiveProcess
    {
        private readonly string? _stderrTail;

        public ScriptedInteractiveProcess(string? stderrTail)
        {
            _stderrTail = stderrTail;
        }

        public Stream StandardInput { get; } = Stream.Null;

        public Stream StandardOutput { get; } = new MemoryStream();

        public Task<string?> GetStandardErrorTailAsync()
        {
            return Task.FromResult(_stderrTail);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public void BuildEnvironment_UnderBwrap_AddsNothingTheChainSets_SoTheArgvHasNoDuplicateSetenv()
    {
        // Linux unchanged: the chain already sets HOME/TMPDIR/TMP/TEMP to the POSIX view, so only the registration's variables are passed.
        var registered = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NODE_ENV"] = "production"
        };

        var environment = AssertEx.NotNull(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(SandboxIsolatedPaths.Posix), registered));
        AssertEx.Null(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(SandboxIsolatedPaths.Posix),
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var argv = SandboxIsolatedChain.Render(ChainInputs(environment), "/usr/bin/node", []);
        var setenvNames = new List<string>();
        for (var index = 0; index < argv.Count - 1; index++)
        {
            if (argv[index] == "--setenv")
            {
                setenvNames.Add(argv[index + 1]);
            }
        }

        AssertEx.Equal(setenvNames.Count, setenvNames.Distinct(StringComparer.Ordinal).Count(), "every --setenv name appears once");
        AssertEx.Contains(setenvNames, "NODE_ENV");
    }

    [Test]
    public void BuildEnvironment_UnderTheAppContainerBoundary_PointsTheScratchAtTheJailsHostPaths()
    {
        var hostJail = new SandboxIsolatedPaths
        {
            Work = "/host/jail",
            Home = "/host/jail/home",
            Temp = "/host/jail/.tmp"
        };

        var environment = AssertEx.NotNull(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(hostJail),
            new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Equal(hostJail.Home, environment["HOME"]);
        AssertEx.Equal(hostJail.Temp, environment["TMPDIR"]);
    }

    [Test]
    public void BuildEnvironment_ForAnIsolatedHandleWithoutPaths_Throws()
    {
        _ = AssertEx.Throws<InvalidOperationException>(() => SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(paths: null),
            new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Test]
    public void JailSearchPath_DefaultsToTheChainPathOnLinux_AndToTheHostPathOnWindows()
    {
        var record = StdioRecord(McpTrustTier.Sandboxed);

        AssertEx.Equal("/usr/bin:/bin", SandboxedMcpStdioTransport.JailSearchPath(record, windowsHost: false), "the Linux default is unchanged");
        AssertEx.Equal(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, SandboxedMcpStdioTransport.JailSearchPath(record, windowsHost: true),
            "the AppContainer boundary has no namespace of its own, so the child resolves against the PATH the provider passes through");

        var withPath = record with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "/opt/server/bin"
            }
        };
        AssertEx.Equal("/opt/server/bin", SandboxedMcpStdioTransport.JailSearchPath(withPath, windowsHost: true));
    }

    [Test]
    public void ResolveExecutablePath_OnWindowsTriesPathExtAfterTheExactName_AndLinuxLooksTheNameUpExactly()
    {
        var withExe = CreateBindableDirectory("jail-pathext-");
        var withBoth = CreateBindableDirectory("jail-pathext-");
        try
        {
            var exe = CreateExecutable(withExe, "server.exe");
            var exact = CreateExecutable(withBoth, "server");
            _ = CreateExecutable(withBoth, "server.exe");
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PATHEXT"] = ".COM; .exe"
                }
            };
            var windows = SandboxedMcpStdioTransport.ExecutableExtensions(record, windowsHost: true);
            var linux = SandboxedMcpStdioTransport.ExecutableExtensions(record, windowsHost: false);

            AssertEx.Equal(".COM|.exe", string.Join('|', windows), "the registration's PATHEXT wins over the host's");
            AssertEx.Equal(exe, SandboxedMcpStdioTransport.ResolveExecutablePath("server", withExe.FullName, windows));
            AssertEx.Equal(exe, SandboxedMcpStdioTransport.ResolveExecutablePath(Path.Combine(withExe.FullName, "server"), string.Empty, windows));
            AssertEx.Equal(exact, SandboxedMcpStdioTransport.ResolveExecutablePath("server", withBoth.FullName, windows), "the exact name is preferred");
            AssertEx.Empty(linux);
            AssertEx.Null(SandboxedMcpStdioTransport.ResolveExecutablePath("server", withExe.FullName, linux), "Linux never appends an extension");
        }
        finally
        {
            withExe.Delete(recursive: true);
            withBoth.Delete(recursive: true);
        }
    }

    private static SandboxIsolatedChainInputs ChainInputs(IReadOnlyDictionary<string, string> environment) =>
        new()
        {
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            BwrapPath = "/usr/bin/bwrap",
            ScopeUnitName = "xe-mcp-0123456789abcdef0123456789abcdef.scope",
            RuntimeMaxSeconds = 150,
            UserId = 1000,
            GroupId = 1000,
            UsrMergeEntries = [],
            PasswdDescriptor = 10,
            GroupDescriptor = 11,
            NameServiceSwitchDescriptor = 12,
            HostsDescriptor = 13,
            JailDescriptor = 20,
            JailTempDescriptor = 21,
            ThreadLimit = 2,
            AdditionalEnvironment = environment
        };

    private static SandboxHandle IsolatedHandle(SandboxIsolatedPaths? paths)
    {
        return new SandboxHandle
        {
            ProviderName = "process",
            SandboxId = "sandbox",
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "process",
                RuntimeProfile = SandboxedMcpStdioTransport.RuntimeProfile,
                ManifestVersion = 1
            },
            CreatedAt = DateTimeOffset.UnixEpoch,
            ManifestVersion = 1,
            Isolation = SandboxIsolationMode.Filesystem,
            IsolatedPaths = paths
        };
    }

    private static McpServerRecord StdioRecord(McpTrustTier tier)
    {
        return new McpServerRecord
        {
            Id = Guid.NewGuid(),
            Name = "Local",
            Description = null,
            TransportKind = McpTransportKind.Stdio,
            Command = "node",
            Arguments = ["server.js"],
            WorkingDirectory = null,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Url = null,
            TrustTier = tier,
            Enabled = true,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0
        };
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
            return Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner",
                NodeId = "node"
            });
        }
    }
}
