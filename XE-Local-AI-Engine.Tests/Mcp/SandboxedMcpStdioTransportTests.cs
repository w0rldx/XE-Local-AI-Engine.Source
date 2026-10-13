namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
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
            },
            readOnlyTrees: []);

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
    public async Task ConnectAsync_MissingAbsoluteCommand_SaysItDoesNotExistOrIsNotReadable_AndNeverPrintsThePath()
    {
        // An absolute command never consults PATH, and the message reaches the UI: printing PATH would leak this node's directory names.
        var marker = Path.Combine(FixtureRoot, $"path-marker-{Guid.NewGuid():N}");
        var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/nonexistent/xe-mcp-missing-server",
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PATH"] = marker
                }
            },
            IsolatingProvider());

        var exception = await AssertEx.ThrowsAsync<FileNotFoundException>(() => transport.ConnectAsync());

        AssertEx.Contains(exception.Message, "'/nonexistent/xe-mcp-missing-server' does not exist or is not readable");
        AssertEx.False(exception.Message.Contains(marker, StringComparison.Ordinal), "the registration's PATH must not be printed");
        AssertEx.False(exception.Message.Contains("PATH", StringComparison.Ordinal), "an absolute command has nothing to do with PATH");
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
    public void ExitCodeHints_NeverSteerTheOperatorToThePrivilegedHostTier()
    {
        // A startup hint is read at the moment a server failed in the sandbox; "use Privileged" there would teach operators to drop the
        // boundary instead of fixing the folder. Read by reflection so a row added later is covered without a matching test row.
        var transport = typeof(SandboxedMcpStdioTransport).GetNestedType("SandboxedTransport", BindingFlags.NonPublic);
        var field = transport?.GetField("ExitCodeHints", BindingFlags.NonPublic | BindingFlags.Static);
        var hints = AssertEx.NotNull(field?.GetValue(null) as IReadOnlyDictionary<uint, string>);

        AssertEx.NotEmpty(hints);
        AssertEx.Empty(hints.Where(entry => entry.Value.Contains("Privileged", StringComparison.OrdinalIgnoreCase)), "no exit-code hint may name the Privileged tier");
    }

    [Test]
    public void ExitCodeHints_AndTheUnlistedCodeHint_EachNameTheUnreadableFolderAndTheRemedy()
    {
        // Read by reflection so a row added later is held to both phrases without a matching test row.
        var transport = AssertEx.NotNull(typeof(SandboxedMcpStdioTransport).GetNestedType("SandboxedTransport", BindingFlags.NonPublic));
        var hints = AssertEx.NotNull(transport.GetField("ExitCodeHints", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IReadOnlyDictionary<uint, string>);
        var unlisted = AssertEx.NotNull(transport.GetField("UnlistedExitHint", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string);

        foreach (var hint in hints.Values.Append(unlisted))
        {
            AssertEx.Contains(hint, "a folder the sandbox cannot read");
            AssertEx.Contains(hint, "Program Files or a folder the sandbox is granted");
            AssertEx.False(hint.Contains("Win32k", StringComparison.OrdinalIgnoreCase), $"a shipped hint must not name Win32k: {hint}");
            AssertEx.False(hint.Contains("Privileged", StringComparison.OrdinalIgnoreCase), $"a hint must not steer to the Privileged tier: {hint}");
        }
    }

    [Test]
    public async Task Handshake_WhenTheServerExitsWithAnUnlistedCodeUnderTheAppContainer_NamesTheGrantedFolderRemedy()
    {
        // Node's EPERM exit is 1: no row of its own, yet under the AppContainer boundary it is still most often an unreadable folder.
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");
            await using var process = new ScriptedInteractiveProcess(stderrTail: null, exitCode: 1);
            var provider = IsolatingProvider(SandboxIsolatedPaths.ForHostJail(Path.Combine(directory.FullName, "jail")));
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ISandboxInteractiveProcess>(process));
            var transport = CreateTransport(StdioRecord(McpTrustTier.Sandboxed) with
                {
                    Command = server
                },
                provider);

            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var exception = await AssertEx.ThrowsAsync<McpServerStartupException>(() => McpClient.CreateAsync(transport, clientOptions: null, NullLoggerFactory.Instance, handshake.Token));

            AssertEx.Contains(exception.Message, "(exit code 0x00000001). Under the sandbox an early exit is often a file in a folder the sandbox cannot read");
            AssertEx.Contains(exception.Message, "Program Files or a folder the sandbox is granted (its command or working directory, on an NTFS volume)");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(0xC0000142u, "exit code 0xC0000142", "a DLL failed to initialise while the process started")]
    [Arguments(0xC06D007Eu, "exit code 0xC06D007E", "it is missing or sits in a folder")]
    [Arguments(0x80008089u, "exit code 0x80008089", "the .NET runtime could not start")]
    [Arguments(0x00000001u, "exit code 0x00000001", null)]
    [Arguments(0x8000809Bu, "exit code 0x8000809B", "could not find or read the application DLL")]
    public async Task Handshake_WhenTheServerExitsNonZeroWithoutStderr_NamesTheExitCodeInHexAndTheSandboxWarnings(uint exitCode, string expectedCode, string? expectedHint)
    {
        // N-3, N-16, N-17 and the 0x8000809B row (.NET host cannot read the app DLL): a child that cannot start dies silently; the exit code,
        // its meaning and MXC's warnings say why. A code with no known meaning gets no hint.
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");
            await using var process = new ScriptedInteractiveProcess(stderrTail: null, unchecked((int)exitCode), ["ProcessContainer cleanup skipped"]);
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

            AssertEx.Contains(exception.Message, expectedCode);
            if (expectedHint is null)
            {
                // Nothing between the code and the warnings: an unmapped code gets no hint.
                AssertEx.Contains(exception.Message, $"({expectedCode}). The sandbox reported:");
            }
            else
            {
                AssertEx.Contains(exception.Message, expectedHint);
                AssertEx.Contains(exception.Message, "a folder the sandbox cannot read");
                AssertEx.Contains(exception.Message, "Program Files or a folder the sandbox is granted");
            }

            AssertEx.Contains(exception.Message, "ProcessContainer cleanup skipped");
            AssertEx.Contains(exception.Message, "wrote nothing to stderr");
            AssertEx.Null(exception.StderrTail);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Connect_WhenTheServerNeverAnswersBeforeTheTimeout_StaysACancellationCarryingTheStartupDiagnosis()
    {
        // The hard-error dialog keeps the dying process alive, so stdout never closes and the connect timeout is all that fires; the
        // factory still names what the sandbox knew, as a cancellation so a caller's own cancel keeps propagating.
        var directory = CreateBindableDirectory("jail-path-");
        try
        {
            var server = CreateExecutable(directory, "xe-server");
            var neverWritten = new Pipe();
            await using var process = new ScriptedInteractiveProcess(stderrTail: null, exitCode: null, ["AppContainer profile reused"], neverWritten.Reader.AsStream());
            var provider = IsolatingProvider();
            provider.StartInteractiveAsync(Arg.Any<SandboxHandle>(), Arg.Any<SandboxCommandRequest>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ISandboxInteractiveProcess>(process));
            var factory = new McpClientFactory(Options.Create(new McpOptions
                {
                    ConnectTimeoutSeconds = 30,
                    HttpLoopbackHosts = ["127.0.0.1"]
                }),
                provider,
                IdentityProvider(),
                NodeDataDirectory(),
                Options.Create(new ComputeOptions()),
                Options.Create(new LocalContainerOptions()),
                new StubNodeRuntimeSettings().Build(),
                NullLoggerFactory.Instance);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var exception = await AssertEx.ThrowsAsync<OperationCanceledException>(() => factory.CreateAsync(StdioRecord(McpTrustTier.Sandboxed) with
                {
                    Command = server
                },
                sessionKey: null,
                timeout.Token));

            var startup = AssertEx.NotNull(exception.InnerException as McpServerStartupException);
            AssertEx.Contains(startup.Message, "did not complete the MCP handshake");
            AssertEx.Contains(startup.Message, "AppContainer profile reused");
            AssertEx.False(startup.Message.Contains("exit code", StringComparison.Ordinal), "a process still running has no exit code to name");
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
    [Arguments("AppData")]
    [Arguments("AppData/Roaming")]
    [Arguments("AppData/Local")]
    [Arguments("AppData/Roaming/Microsoft")]
    [Arguments("AppData/Local/Microsoft")]
    [Arguments("AppData/Local/Google")]
    [Arguments("AppData/Local/Mozilla")]
    [Arguments("AppData/Roaming/Mozilla")]
    public void ResolveReadOnlyTrees_ForAWorkingDirectoryThatIsOrHoldsAWindowsProfileStore_IsRefused(string relative)
    {
        // The exit-code hints steer operators to "a granted folder": AppData as that folder would hand the server DPAPI master keys,
        // Credential Manager and browser profiles. The parents are refused by the ancestor rule.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var denied = Path.Combine([home, .. relative.Split('/')]);
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = denied
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
            $"binding '{denied}' would hand the server the operator's Windows profile stores.");

        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(denied));
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAnNpmGlobalFolderUnderAppDataRoaming_IsAllowed()
    {
        // The other half: a global npm install lives in AppData\Roaming\npm, beside the stores but holding none of them.
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var npm = Directory.CreateDirectory(Path.Combine(home.FullName, "AppData", "Roaming", "npm"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "npx",
                WorkingDirectory = null
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                _ => Path.Combine(npm.FullName, "npx"),
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName));

            AssertEx.Equal(expected: 1, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(npm.FullName), trees[0]);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(".ssh/keys")]
    [Arguments(".config/gh")]
    [Arguments("AppData/Roaming/Microsoft/Protect")]
    [Arguments("AppData/Roaming/Microsoft/Credentials")]
    [Arguments("AppData/Roaming/Mozilla/Firefox/Profiles")]
    [Arguments("AppData/Local/Microsoft/Windows")]
    [Arguments("AppData/Local/Microsoft/WindowsApps-lookalike")]
    public void ResolveReadOnlyTrees_ForATreeBeneathASubtreeDeniedStore_IsRefused(string relative)
    {
        // A folder inside a credential store is still the store: the round-5 tester read key4.db through a Firefox Profiles grant.
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine([home.FullName, .. relative.Split('/')]));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = inside.FullName
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                    path => path,
                    SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName)),
                $"'{relative}' lies inside a credential store.");

            AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(inside.FullName));
            AssertEx.Contains(exception.Message, "lies inside the sensitive host path");
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(@"\\?\C:\Users\u\AppData\Roaming\Mozilla\Firefox\Profiles\x")]
    [Arguments(@"\\.\C:\Users\u\AppData\Roaming\Mozilla\Firefox\Profiles\x")]
    [Arguments(@"\\?\Volume{0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0}\Users\u\AppData\Roaming\Mozilla")]
    [Arguments(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Users\u\AppData\Roaming\Mozilla")]
    [Arguments(@"\\?\UNC\server\share\Users\u")]
    [Arguments(@"\\server\share\Users\u")]
    [Arguments(@"C:\Users\u\AppData\Roaming\Mozilla::$INDEX_ALLOCATION\Firefox\Profiles")]
    [Arguments(@"C:\Users\u\AppData\Roaming\Mozilla:$I30:$INDEX_ALLOCATION")]
    [Arguments(@"C:")]
    [Arguments(@"C:relative\folder")]
    [Arguments(@"1:\folder")]
    public void IsPlainDriveLetterPath_RefusesEveryDeviceVolumeShareAndStreamSpelling(string spelling)
    {
        // Each names a store without starting with its drive-letter path, so a Windows tree spelled this way is refused before any comparison.
        AssertEx.False(SandboxedMcpStdioTransport.IsPlainDriveLetterPath(spelling), $"'{spelling}' must not count as a plain drive-letter path.");
    }

    [Test]
    [Arguments(@"C:\Program Files\nodejs")]
    [Arguments(@"c:\Users\u\AppData\Roaming\npm")]
    [Arguments(@"G:\")]
    public void IsPlainDriveLetterPath_AcceptsAnOrdinaryFolderPath(string path)
    {
        AssertEx.True(SandboxedMcpStdioTransport.IsPlainDriveLetterPath(path), $"'{path}' is an ordinary folder path.");
    }

    [Test]
    [Arguments(@"D:\", true)]
    [Arguments(@"C:\", true)]
    [Arguments("/", true)]
    [Arguments(@"C:\xe\sub", false)]
    [Arguments(@"D:\x", false)]
    [Arguments("/usr", false)]
    public void IsVolumeRoot_IsTrueOnlyForTheRootOfADrive(string canonical, bool expected)
    {
        // Round 6 read a whole data drive through a D:\ working directory: no denied root lay on D:, so nothing matched.
        AssertEx.Equal(expected, SandboxedMcpStdioTransport.IsVolumeRoot(canonical), $"'{canonical}'");
    }

    [Test]
    public void ResolveReadOnlyTrees_ForTheFilesystemRoot_IsRefusedAsAVolumeRoot()
    {
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = "/"
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

        AssertEx.Contains(exception.Message, "which is a volume root");
        AssertEx.False(exception.Message.Contains("home directory", StringComparison.Ordinal), "the volume-root guard runs before the denylist.");
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAnExistingFolderWhoseRealNameCannotBeRead_IsRefusedNotDropped()
    {
        // A chain of MaxLinkHops links resolves for the OS but exhausts the walk, the Linux twin of an existing UNC share whose final
        // path is refused: the folder exists, its real name is unknown, and a silent drop started the server without it.
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("drives the null-canonical path through a symlink chain; directory symlinks are privileged on Windows.");
        }

        var chain = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"chain-{Guid.NewGuid():N}"));
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(chain.FullName, "real")).FullName;
            // Linux follows at most 40 links (ELOOP beyond), so this chain stays resolvable only while MaxLinkHops is at most 40.
            for (var hop = 1; hop <= SandboxedMcpStdioTransport.MaxLinkHops; hop++)
            {
                var link = Path.Combine(chain.FullName, $"l{hop.ToString(CultureInfo.InvariantCulture)}");
                Directory.CreateSymbolicLink(link, target);
                target = link;
            }

            AssertEx.True(Directory.Exists(target), "the OS still resolves a chain of MaxLinkHops links.");
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = target
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()));

            AssertEx.Contains(exception.Message, target);
            AssertEx.Contains(exception.Message, "a link chain too deep to resolve or a folder whose real name cannot be read");
            AssertEx.Contains(exception.Message, "binds only folders it can resolve");
            AssertEx.False(exception.Message.Contains("NTFS", StringComparison.Ordinal), "the Windows wording must not reach a Linux operator.");
        }
        finally
        {
            chain.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAFolderHoldingSeveralStores_NamesTheContainedStoresNotLiesInside()
    {
        // %APPDATA% holds seven stores: the refusal says it contains them, not that it "lies inside" the first one listed.
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var roaming = Directory.CreateDirectory(Path.Combine(home.FullName, "AppData", "Roaming"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = roaming.FullName
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                path => path,
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName)));

            AssertEx.Contains(exception.Message, $"protected folders (first: '{Path.Combine(roaming.FullName, "Microsoft")}')");
            AssertEx.False(exception.Message.Contains("lies inside", StringComparison.Ordinal), exception.Message);
            AssertEx.False(exception.Message.Contains("home directory", StringComparison.Ordinal), exception.Message);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAFolderHoldingOneStore_NamesThatStore()
    {
        // ~/.local/share holds exactly one store (the keyrings), so the refusal names it instead of counting "1 protected folder".
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var share = Directory.CreateDirectory(Path.Combine(home.FullName, ".local", "share"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = share.FullName
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                path => path,
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName)));

            AssertEx.Contains(exception.Message, $"contains the protected folder '{Path.Combine(share.FullName, "keyrings")}'");
            AssertEx.False(exception.Message.Contains("first:", StringComparison.Ordinal), exception.Message);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForAFolderHoldingTheHomeDirectory_SaysItContainsTheHomeDirectory()
    {
        var parent = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"users-{Guid.NewGuid():N}"));
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(parent.FullName, "operator"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = parent.FullName
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                path => path,
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName)));

            AssertEx.Contains(exception.Message, $"is or contains the home directory '{home.FullName}'");
            AssertEx.False(exception.Message.Contains("root itself", StringComparison.Ordinal), exception.Message);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForACommandBeneathASubtreeDeniedStore_IsRefused()
    {
        // The command axis passes the same gate: a binary inside AppData\Roaming\Microsoft would grant its folder of that store.
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(home.FullName, "AppData", "Roaming", "Microsoft", "Tools"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "server",
                WorkingDirectory = null
            };

            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                _ => Path.Combine(bin.FullName, "server.exe"),
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName)));

            AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(bin.FullName));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Test]
    public void ResolveReadOnlyTrees_ForATreeBeneathTheNodeDataRoot_IsRefused()
    {
        var record = StdioRecord(McpTrustTier.Sandboxed) with
        {
            Command = "/usr/bin/server-binary",
            WorkingDirectory = Path.Combine(NodeDataRoot, "sandboxes", "jail")
        };

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, SensitiveRoots()),
            "the node database, keys and every jail live beneath the data root.");

        AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(NodeDataRoot));
    }

    [Test]
    public void ResolveReadOnlyTrees_ForTheWindowsAppsAliasFolder_IsAllowed()
    {
        // The Store execution-alias folder holds alias stubs and no secrets; it and its package folders stay bindable.
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var aliases = Directory.CreateDirectory(Path.Combine(home.FullName, "AppData", "Local", "Microsoft", "WindowsApps"));
            var package = Directory.CreateDirectory(Path.Combine(aliases.FullName, "Microsoft.PowerShell_8wekyb3d8bbwe"));
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "pwsh.exe",
                WorkingDirectory = package.FullName
            };

            var trees = SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record,
                _ => Path.Combine(aliases.FullName, "pwsh.exe"),
                SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName));

            AssertEx.Equal(expected: 2, trees.Count);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(aliases.FullName), trees[0]);
            AssertEx.Equal(Path.TrimEndingDirectorySeparator(package.FullName), trees[1]);
        }
        finally
        {
            home.Delete(recursive: true);
        }
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
                new SandboxedMcpStdioTransport.SensitiveHostRoot
                {
                    HostPath = Path.Combine(account.FullName, ".ssh"),
                    DeniesSubtree = false
                }
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
    public void BuildSensitiveHostRoots_WithAHomeDirectory_CarriesEveryCredentialStore_InItsClass()
    {
        var home = Path.GetFullPath("/srv/accounts/xe");
        var roots = SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home);

        string Under(params string[] parts) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine([home, .. parts])));
        void Expect(string path, bool deniesSubtree) =>
            AssertEx.True(roots.Any(root => root.HostPath == Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) && root.DeniesSubtree == deniesSubtree),
                $"'{path}' must be on the list with DeniesSubtree = {deniesSubtree}.");

        Expect(home, deniesSubtree: false);
        foreach (var store in new[] { ".ssh", ".gnupg", ".aws", ".azure", ".config", ".docker", ".kube", ".mozilla", ".thunderbird", ".password-store", ".pki" })
        {
            Expect(Under(store), deniesSubtree: true);
        }

        Expect(Under(".local", "share", "keyrings"), deniesSubtree: true);
        foreach (var store in new[] { "Microsoft", "Mozilla", "Thunderbird", "Opera Software", "gnupg", "GitHub CLI", "gcloud" })
        {
            Expect(Under("AppData", "Roaming", store), deniesSubtree: true);
        }

        foreach (var store in new[] { "Google", "Mozilla", "BraveSoftware", "Vivaldi" })
        {
            Expect(Under("AppData", "Local", store), deniesSubtree: true);
        }

        Expect(NodeDataRoot, deniesSubtree: true);
        Expect(AppContext.BaseDirectory, deniesSubtree: true);
        Expect("/root", deniesSubtree: true);
        Expect("/etc", deniesSubtree: true);
        Expect("/var", deniesSubtree: true);
        AssertEx.Contains(roots, new SandboxedMcpStdioTransport.SensitiveHostRoot
        {
            HostPath = Under("AppData", "Local", "Microsoft"),
            DeniesSubtree = true,
            AllowedBeneath = Under("AppData", "Local", "Microsoft", "WindowsApps")
        });
        AssertEx.Empty(roots.Where(root => root.AllowedBeneath is not null
                                           && !string.Equals(root.AllowedBeneath, Path.Combine(root.HostPath, "WindowsApps"), StringComparison.Ordinal)),
            "a carve-out is only ever the store's own WindowsApps folder");
        var localMicrosoft = new[]
        {
            Under("AppData", "Local", "Microsoft"),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft")))
        };
        AssertEx.Empty(roots.Where(root => root.AllowedBeneath is not null && !localMicrosoft.Contains(root.HostPath, StringComparer.Ordinal)),
            "the WindowsApps carve-out belongs to the local Microsoft store alone");
    }

    [Test]
    public void BuildSensitiveHostRoots_AlsoDeniesTheStoresAtTheProfileFoldersActualLocation()
    {
        // Folder redirection moves AppData away from the home directory; its stores must stay denied where they really are.
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(roaming) || string.IsNullOrWhiteSpace(local))
        {
            throw new SkipTestException("this host reports no application-data folders to re-root the stores at.");
        }

        var roots = SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, static () => Path.GetFullPath("/srv/accounts/xe"));

        foreach (var store in new[] { Path.Combine(roaming, "Microsoft"), Path.Combine(roaming, "Mozilla"), Path.Combine(local, "Google"), Path.Combine(local, "Microsoft") })
        {
            var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store));
            AssertEx.True(roots.Any(root => root.HostPath == lexical && root.DeniesSubtree), $"'{lexical}' must be denied with its subtree.");
        }
    }

    [Test]
    public void BuildSensitiveHostRoots_WhenWindowsAppsIsALink_DropsTheCarveOut()
    {
        // A junction from WindowsApps into Credentials would otherwise move the exception onto the store it points at.
        RequireSymlinks();
        var home = Directory.CreateDirectory(Path.Combine(FixtureRoot, $"home-{Guid.NewGuid():N}"));
        try
        {
            var microsoft = Directory.CreateDirectory(Path.Combine(home.FullName, "AppData", "Local", "Microsoft"));
            var credentials = Directory.CreateDirectory(Path.Combine(microsoft.FullName, "Credentials"));
            var aliases = Path.Combine(microsoft.FullName, "WindowsApps");
            Directory.CreateSymbolicLink(aliases, credentials.FullName);
            var roots = SandboxedMcpStdioTransport.BuildSensitiveHostRoots(NodeDataRoot, () => home.FullName);
            var record = StdioRecord(McpTrustTier.Sandboxed) with
            {
                Command = "/usr/bin/server-binary",
                WorkingDirectory = aliases
            };

            AssertEx.Empty(roots.Where(root => root.HostPath == Path.TrimEndingDirectorySeparator(microsoft.FullName) && root.AllowedBeneath is not null),
                "a linked WindowsApps must not carry the carve-out");
            var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() => SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, path => path, roots));
            AssertEx.Contains(exception.Message, Path.TrimEndingDirectorySeparator(credentials.FullName));
        }
        finally
        {
            home.Delete(recursive: true);
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
    private static IReadOnlyList<SandboxedMcpStdioTransport.SensitiveHostRoot> SensitiveRoots()
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

    private static IAgentSandboxRuntimeProvider IsolatingProvider(SandboxIsolatedPaths? isolatedPaths = null)
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
                    ManifestVersion = 1,
                    Isolation = isolatedPaths is null ? default : SandboxIsolationMode.Filesystem,
                    IsolatedPaths = isolatedPaths
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

    /// <summary>
    ///     A server whose stderr tail, exit code and sandbox warnings are scripted. Its stdout is at end of stream (it has exited) unless
    ///     a stream that never ends is passed (it hangs, as a process held alive by a Windows hard-error dialog does).
    /// </summary>
    private sealed class ScriptedInteractiveProcess : ISandboxInteractiveProcess
    {
        private readonly string? _stderrTail;

        public ScriptedInteractiveProcess(string? stderrTail, int? exitCode = null, IReadOnlyList<string>? warnings = null, Stream? standardOutput = null)
        {
            _stderrTail = stderrTail;
            ExitCode = exitCode;
            Warnings = warnings ?? [];
            StandardOutput = standardOutput ?? new MemoryStream();
        }

        public Stream StandardInput { get; } = Stream.Null;

        public Stream StandardOutput { get; }

        public int? ExitCode { get; }

        public IReadOnlyList<string> Warnings { get; }

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
    public void BuildEnvironment_UnderAHostPathView_PreservesNodeSymlinks_UnlessTheRegistrationSetsNodeOptions()
    {
        // Node realpaths its script and modules, stat-ing every ancestor; the AppContainer cannot stat C:\Users, so that walk is skipped.
        var hostJail = SandboxIsolatedPaths.ForHostJail(Path.Combine(FixtureRoot, "jail"));
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        var own = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NODE_OPTIONS"] = "--max-old-space-size=512"
        };

        var defaulted = AssertEx.NotNull(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(hostJail), empty));
        var overridden = AssertEx.NotNull(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(hostJail), own));
        var posix = SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(SandboxIsolatedPaths.Posix), empty);

        AssertEx.Equal("--preserve-symlinks --preserve-symlinks-main", defaulted["NODE_OPTIONS"]);
        AssertEx.Equal("--max-old-space-size=512", overridden["NODE_OPTIONS"], "the registration's own value wins");
        var mixedCase = AssertEx.NotNull(SandboxedMcpStdioTransport.BuildEnvironment(IsolatedHandle(hostJail), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Node_Options"] = "--inspect=0"
        }));
        AssertEx.Equal(expected: 1, mixedCase.Keys.Count(key => string.Equals(key, "NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)),
            "Windows reads environment names case-insensitively, so a differently cased key must replace the default");
        AssertEx.Equal("--inspect=0", mixedCase["NODE_OPTIONS"]);
        AssertEx.Null(posix, "the bwrap view resolves through a namespace that can stat its ancestors, so it gets no default");
    }

    [Test]
    public void BuildCommandRequest_UsesTheRegisteredWorkingDirectoryAsTheCwd_OnlyWhenItIsAGrantedTree()
    {
        // A relative script path resolves against the cwd, so the granted working directory is the cwd; anything not granted leaves the jail.
        var packages = CreateBindableDirectory("xe-mcp-cwd-");
        try
        {
            var handle = IsolatedHandle(SandboxIsolatedPaths.Posix);
            string? CwdFor(string? workingDirectory, IReadOnlyList<string>? granted = null)
            {
                var record = StdioRecord(McpTrustTier.Sandboxed) with
                {
                    WorkingDirectory = workingDirectory
                };
                var trees = granted ?? SandboxedMcpStdioTransport.ResolveReadOnlyTrees(record, static _ => null, SensitiveRoots());
                return CreateTransport(record, IsolatingProvider()).BuildCommandRequest(handle, trees).WorkingDirectory;
            }

            AssertEx.Equal(Path.TrimEndingDirectorySeparator(packages.FullName), CwdFor(packages.FullName + Path.DirectorySeparatorChar + "."));
            AssertEx.Null(CwdFor(null), "no registered working directory: the jail is the cwd");
            AssertEx.Null(CwdFor(Path.Combine(packages.FullName, "missing")), "a directory that is not granted is never the cwd");
            AssertEx.Null(CwdFor("/usr"), "a tree the chain owns is dropped from the grants, so it is not the cwd either");
            AssertEx.Null(CwdFor(packages.FullName, granted: []), "only the trees the sandbox was created with count, never a second resolution");
        }
        finally
        {
            packages.Delete(recursive: true);
        }
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
