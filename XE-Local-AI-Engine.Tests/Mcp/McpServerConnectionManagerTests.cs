namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
// System.ComponentModel declares its own CategoryAttribute, and a file-scoped using beats the global one.
using CategoryAttribute = CategoryAttribute;

/// <summary>
///     Drives the real connection manager against an in-process MCP server (genuine SDK protocol over an in-memory
///     stream pair) to prove register -> discover -> offer -> execute, plus the manager's reconcile, qualified-name,
///     approval-wrap, deterministic-order, failure-isolation, status, and dispose behavior. No real process or socket.
///     Serialized: each test runs one or more live server loops over stream pipes, and running them concurrently
///     contends on the shared pumping machinery.
/// </summary>
[NotInParallel(nameof(McpServerConnectionManagerTests))]
[Category(TestCategories.Unit)]
public sealed class McpServerConnectionManagerTests
{
    [Test]
    public async Task RefreshAsync_ConnectsEnabledServer_PublishesQualifiedApprovalWrappedTools()
    {
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), record);

        await manager.RefreshAsync();

        // Assert the connection succeeded FIRST: the manager isolates a failed connect/list by contributing zero tools,
        // so checking Connected (+ surfacing LastError) turns any transient handshake failure into a clear diagnostic
        // instead of a confusing empty-snapshot assertion downstream.
        var status = manager.GetStatuses().Single(s => s.ServerId == record.Id);
        AssertEx.True(status.Connected, $"the enabled server must connect (LastError: {status.LastError ?? "none"})");

        var descriptors = registry.GetDescriptors();
        AssertEx.Contains(descriptors.Select(static d => d.Name), "mcp__weather__get_forecast");
        AssertEx.True(descriptors.All(static d => d.RequiresApproval), "every MCP tool defaults to requiring approval");
        AssertEx.True(registry.TryResolve("mcp__weather__get_forecast", out var executable));
        AssertEx.True(executable is ApprovalRequiredAIFunction, "the executable must be approval-wrapped");

        // The per-server status carries the discovered tool list (qualified name + description + approval) for the UI.
        AssertEx.Equal(expected: 1, status.ToolCount);
        AssertEx.Equal(status.ToolCount, status.Tools.Count);
        var tool = status.Tools.Single();
        AssertEx.Equal("mcp__weather__get_forecast", tool.Name);
        AssertEx.True(tool.RequiresApproval, "the per-server tool list defaults to approval-on");
        AssertEx.NotNullOrEmpty(tool.Description);
    }

    [Test]
    public async Task RefreshAsync_PrivilegedHostStdioServer_OffersItsToolsAsWriteExecute()
    {
        // A PrivilegedHost stdio server is launched by this node as an unconfined child of its own user, so its tools
        // can write files and run commands here. Network — "reaches an out-of-process surface" — understates that, and
        // the category is what the operator's badge, the node approval policy and every audit row read.
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather") with
        {
            TrustTier = McpTrustTier.PrivilegedHost
        };
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), record);

        await manager.RefreshAsync();

        var status = manager.GetStatuses().Single(s => s.ServerId == record.Id);
        AssertEx.True(status.Connected, $"the enabled server must connect (LastError: {status.LastError ?? "none"})");

        var descriptor = registry.GetDescriptors().Single(static d => d.Name == "mcp__weather__get_forecast");
        AssertEx.Equal(ToolCategory.WriteExecute, descriptor.Category);
        // The tier raises the CLASS, never lowers the gate: approval is unchanged and the structural pre-wrap stays.
        AssertEx.True(descriptor.RequiresApproval, "a PrivilegedHost tool still requires approval");
        AssertEx.True(registry.TryResolve("mcp__weather__get_forecast", out var executable));
        AssertEx.True(executable is ApprovalRequiredAIFunction, "the executable must still be approval-wrapped");
    }

    [Test]
    public async Task RefreshAsync_SandboxedStdioServer_OffersItsToolsAsNetwork()
    {
        // The default tier grants no host reach, so Network is the whole story — the same class every MCP tool has
        // carried since before the tiers existed.
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather") with
        {
            TrustTier = McpTrustTier.Sandboxed
        };
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), record);

        await manager.RefreshAsync();

        var descriptor = registry.GetDescriptors().Single(static d => d.Name == "mcp__weather__get_forecast");
        AssertEx.Equal(ToolCategory.Network, descriptor.Category);
    }

    [Test]
    public async Task RefreshAsync_HttpServer_OffersItsToolsAsNetworkWhateverTheStoredTierSays()
    {
        // The tier is inert for HTTP: this node launches nothing, so a stored PrivilegedHost (which the service
        // normalizes away on write, but which a hand-edited row could still carry) must not raise the class.
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather") with
        {
            TransportKind = McpTransportKind.Http,
            Command = null,
            Url = "http://127.0.0.1:8931/sse",
            TrustTier = McpTrustTier.PrivilegedHost
        };
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), record);

        await manager.RefreshAsync();

        var descriptor = registry.GetDescriptors().Single(static d => d.Name == "mcp__weather__get_forecast");
        AssertEx.Equal(ToolCategory.Network, descriptor.Category);
    }

    [Test]
    public async Task RefreshAsync_DisabledServer_ContributesNothing()
    {
        // The store's ListEnabledAsync excludes disabled rows, so a disabled server is simply never connected.
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory());

        await manager.RefreshAsync();

        AssertEx.Equal(expected: 0, registry.GetDescriptors().Count);
        AssertEx.Equal(expected: 0, manager.GetStatuses().Count);
    }

    [Test]
    public async Task RefreshAsync_ServerThatFailsToConnect_IsIsolatedFromHealthyServers()
    {
        await using var healthy = await InProcMcpServer.StartAsync("healthy",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var healthyRecord = StdioRecord("Healthy");
        var brokenRecord = StdioRecord("Broken");

        var factory = new FakeMcpClientFactory((healthyRecord.Id, healthy.Client));
        factory.FailFor(brokenRecord.Id);

        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, healthyRecord, brokenRecord);

        await manager.RefreshAsync();

        AssertEx.Contains(registry.GetDescriptors().Select(static d => d.Name), "mcp__healthy__get_forecast");

        var statuses = manager.GetStatuses();
        var healthyStatus = statuses.Single(s => s.ServerId == healthyRecord.Id);
        var brokenStatus = statuses.Single(s => s.ServerId == brokenRecord.Id);
        AssertEx.True(healthyStatus.Connected);
        AssertEx.Equal(expected: 1, healthyStatus.Tools.Count);
        AssertEx.False(brokenStatus.Connected);
        AssertEx.NotNull(brokenStatus.LastError);
        AssertEx.Equal(expected: 0, brokenStatus.Tools.Count);
    }

    [Test]
    public async Task RefreshAsync_ServerThrowingHttpRequestException_IsIsolated()
    {
        // MED-1: an HTTP transport failure (HttpRequestException) must be caught per-server, not escape and abort the
        // whole refresh, so the healthy server still loads and the snapshot stays consistent.
        await using var healthy = await InProcMcpServer.StartAsync("healthy",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var healthyRecord = StdioRecord("Healthy");
        var brokenRecord = StdioRecord("Broken");

        var factory = new FakeMcpClientFactory((healthyRecord.Id, healthy.Client));
        factory.FailFor(brokenRecord.Id, static () => new HttpRequestException("Simulated HTTP transport failure."));

        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, healthyRecord, brokenRecord);

        await manager.RefreshAsync();

        AssertEx.Contains(registry.GetDescriptors().Select(static d => d.Name), "mcp__healthy__get_forecast");
        var statuses = manager.GetStatuses();
        AssertEx.True(statuses.Single(s => s.ServerId == healthyRecord.Id).Connected);
        var broken = statuses.Single(s => s.ServerId == brokenRecord.Id);
        AssertEx.False(broken.Connected);
        AssertEx.NotNull(broken.LastError);
        AssertEx.Equal(McpConnectionFailureReason.Transport, broken.FailureReason);
    }

    [Test]
    public async Task RefreshAsync_PrivilegedHostCommandThatCannotStart_ReportsServerNotFoundWithoutThePath()
    {
        // The SDK's stdio launch wraps the failed Process.Start in an IOException; SandboxedMcpStdioTransportTests pins that shape.
        var status = await RefreshWithFailureAsync(static () => new IOException("Failed to connect transport.",
            new Win32Exception(2, "An error occurred trying to start process '/opt/secret/server'.")));

        AssertEx.Equal(McpConnectionFailureReason.ServerNotFound, status.FailureReason);
        AssertEx.Equal("The MCP server's command was not found or could not be started.", status.LastError);
    }

    [Test]
    public async Task RefreshAsync_SandboxedCommandMissingOnTheJailPath_ReportsServerNotFoundWithTheTransportsHint()
    {
        // The Sandboxed transport's pre-check names the jail PATH, the only clue that the host PATH does not apply there.
        const string hint = "The MCP server 'x' command 'codegraph' was not found on the sandbox PATH (/usr/bin:/bin). The sandbox does not see this node's PATH.";
        var status = await RefreshWithFailureAsync(static () => new FileNotFoundException(hint));

        AssertEx.Equal(McpConnectionFailureReason.ServerNotFound, status.FailureReason);
        AssertEx.Equal(hint, status.LastError);
    }

    [Test]
    public async Task RefreshAsync_HintQuotingTheRegistrationsPath_KeepsThePath_AndRedactsABareBearerToken()
    {
        // PATH was scrubbed as a "secret", blanking the one clue in the hint; a "Bearer <token>" value never matched the bare token.
        const string path = "/opt/xe-server/bin:/usr/bin";
        const string token = "tok-abcdef0123456789";
        var hint = $"The MCP server 'x' command 'server' was not found on the sandbox PATH ({path}). Token {token} was not used.";
        var status = await RefreshWithFailureAsync(() => new FileNotFoundException(hint),
            environment: new Dictionary<string, string>
            {
                ["PATH"] = path,
                ["AUTH"] = $"Bearer {token}"
            });

        AssertEx.Equal($"The MCP server 'x' command 'server' was not found on the sandbox PATH ({path}). Token [REDACTED] was not used.", status.LastError);
    }

    private const string SandboxRefusedMessage =
        "The sandbox refused to start the MCP server: its command or working directory overlaps a protected location, or the sandbox boundary could not be established. Point it at the directory holding the server's own files.";

    private const string SandboxUnavailableMessage =
        "This node cannot isolate the MCP server from the host filesystem. Install bubblewrap (bwrap) with user-namespace support, or move the server to the Privileged host tier.";

    [Test]
    public async Task RefreshAsync_SandboxRefusalOnAnIsolationCapableNode_ReportsSandboxRefusedWithTheFixedMessage()
    {
        var status = await RefreshWithFailureAsync(static () => new SandboxCapabilityNotSupportedException("The sandbox command could not be launched."), IsolationCapableProvider());

        AssertEx.Equal(McpConnectionFailureReason.SandboxRefused, status.FailureReason);
        AssertEx.Equal(SandboxRefusedMessage, status.LastError);
    }

    [Test]
    public async Task RefreshAsync_SandboxRefusalNamingAHostPath_NeverReportsThePath()
    {
        // The shape SandboxedMcpStdioTransport throws for a denied root: both paths stay in the server log, never in LastError.
        var status = await RefreshWithFailureAsync(
            static () => new SandboxCapabilityNotSupportedException(
                "The MCP server 'x' is registered at the Sandboxed trust tier and would bind '/home/operator' into its sandbox, which contains the sensitive host path '/home/operator/.ssh'."),
            IsolationCapableProvider());

        AssertEx.Equal(SandboxRefusedMessage, status.LastError);
        AssertEx.False(status.LastError!.Contains("/home/operator", StringComparison.Ordinal));
    }

    [Test]
    public async Task RefreshAsync_SandboxRefusalOnANodeThatCannotIsolate_ReportsSandboxUnavailable()
    {
        // The fake backend advertises no filesystem isolation: the shape of a Windows node or a Linux node without bubblewrap.
        var status = await RefreshWithFailureAsync(static () => new SandboxCapabilityNotSupportedException("This node cannot sandbox."));

        AssertEx.Equal(McpConnectionFailureReason.SandboxUnavailable, status.FailureReason);
        AssertEx.Equal(SandboxUnavailableMessage, status.LastError);
    }

    /// <summary>
    ///     A refusal the <c>high</c> profile caused gets its own reason and fixed text naming the profile and remedy (ADR 0020): this
    ///     isolation-capable backend advertises no ceilings, which <c>high</c> requires.
    /// </summary>
    [Test]
    public async Task RefreshAsync_SandboxRefusalUnderTheHighProfile_ReportsSandboxRefusedByProfile()
    {
        var status = await RefreshWithFailureAsync(static () => new SandboxCapabilityNotSupportedException("refused"), IsolationCapableProvider(),
            profile: SandboxSecurityProfile.High);

        AssertEx.Equal(McpConnectionFailureReason.SandboxRefusedByProfile, status.FailureReason);
        AssertEx.Equal(McpServerConnectionManager.SafeMessage(McpConnectionFailureReason.SandboxRefusedByProfile), status.LastError);
        AssertEx.Contains(status.LastError!, "'high'");
        AssertEx.Contains(status.LastError!, SandboxSecurityProfilePolicy.Remedy);
    }

    // The control: the same refusal under `low` keeps today's reason, so the new one is reported only when the profile's rule refused.
    [Test]
    public async Task RefreshAsync_SandboxRefusalUnderTheLowProfile_KeepsSandboxRefused()
    {
        var status = await RefreshWithFailureAsync(static () => new SandboxCapabilityNotSupportedException("refused"), IsolationCapableProvider(),
            profile: SandboxSecurityProfile.Low);

        AssertEx.Equal(McpConnectionFailureReason.SandboxRefused, status.FailureReason);
    }

    private static IAgentSandboxRuntimeProvider IsolationCapableProvider()
    {
        var provider = Substitute.For<IAgentSandboxRuntimeProvider>();
        provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsFilesystemIsolation);
        return provider;
    }

    [Test]
    public async Task RefreshAsync_ConnectionReset_ReportsTransport()
    {
        var status = await RefreshWithFailureAsync(static () => new SocketException((int)SocketError.ConnectionReset));

        AssertEx.Equal(McpConnectionFailureReason.Transport, status.FailureReason);
    }

    [Test]
    public async Task RefreshAsync_UnclassifiedFailure_KeepsTheGenericMessageAndReason()
    {
        var status = await RefreshWithFailureAsync(static () => new InvalidOperationException("Unsupported MCP transport kind at /home/operator/.secret."));

        AssertEx.Equal(McpConnectionFailureReason.Unknown, status.FailureReason);
        AssertEx.Equal("The MCP server connection failed.", status.LastError);
    }

    [Test]
    public async Task RefreshAsync_McpProtocolFailure_ReportsProtocol()
    {
        var status = await RefreshWithFailureAsync(static () => new McpException("Simulated handshake failure."));

        AssertEx.Equal(McpConnectionFailureReason.Protocol, status.FailureReason);
        AssertEx.Equal("The MCP server did not complete the MCP handshake.", status.LastError);
    }

    [Test]
    public async Task RefreshAsync_StartupExceptionFromTheTransport_ReportsServerStartupFailedWithTheScrubbedTail()
    {
        const string secret = "tok-4f9a8b7c6d5e";
        var status = await RefreshWithFailureAsync(
            static () => new McpServerStartupException("The MCP server 'Broken' exited before completing the MCP handshake.", $"Traceback\nKeyError: API_KEY={secret} rejected"),
            environment: new Dictionary<string, string>
            {
                ["API_KEY"] = secret
            });

        AssertEx.Equal(McpConnectionFailureReason.ServerStartupFailed, status.FailureReason);
        AssertEx.True(status.LastError!.StartsWith("The MCP server 'Broken' exited before completing the MCP handshake.\nstderr: Traceback\nKeyError", StringComparison.Ordinal), status.LastError);
        AssertEx.False(status.LastError.Contains(secret, StringComparison.Ordinal), "the stderr tail is scrubbed of the registration's secret values");
        AssertEx.True(status.LastError.Contains("[REDACTED]", StringComparison.Ordinal), status.LastError);
    }

    [Test]
    public async Task RefreshAsync_ALongTailIsScrubbedBeforeTheDisplayCut_SoASplitSecretCannotLeak()
    {
        // Codex review 2026-09-30: the 1 KB display cut ran before the scrub, so a secret straddling the cut leaked its suffix.
        const string secret = "tok-4f9a8b7c6d5e-abcdef";
        var padding = new string('x', 1024 - 8);
        var tail = $"{padding}{secret} rejected\nmore";
        var status = await RefreshWithFailureAsync(() => new McpServerStartupException("The MCP server 'Broken' exited before completing the MCP handshake.", tail),
            environment: new Dictionary<string, string>
            {
                ["API_KEY"] = secret
            });

        AssertEx.False(status.LastError!.Contains("8b7c6d5e-abcdef", StringComparison.Ordinal), status.LastError);
        AssertEx.True(status.LastError.Contains("[REDACTED] rejected", StringComparison.Ordinal), status.LastError);
    }

    [Test]
    public async Task RefreshAsync_StartupExceptionWhoseMessageAlreadyQuotesTheTail_DoesNotRepeatIt()
    {
        const string tail = "node: not found";
        var status = await RefreshWithFailureAsync(static () =>
            new McpServerStartupException($"The MCP server 'Broken' exited before completing the MCP handshake. Its stderr ended with:\n{tail}", tail));

        AssertEx.Equal(McpConnectionFailureReason.ServerStartupFailed, status.FailureReason);
        AssertEx.Equal($"The MCP server 'Broken' exited before completing the MCP handshake. Its stderr ended with:\n{tail}", status.LastError);
    }

    [Test]
    public async Task Classify_FileNotFound_IsServerNotFound()
    {
        AssertEx.Equal(McpConnectionFailureReason.ServerNotFound, McpServerConnectionManager.Classify(new FileNotFoundException("missing"), hasHeaders: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Classify_Unauthorized_WithoutHeadersIsAuthenticationRequired_WithHeadersIsAuthentication()
    {
        var unauthorized = new HttpRequestException("401", inner: null, HttpStatusCode.Unauthorized);

        AssertEx.Equal(McpConnectionFailureReason.AuthenticationRequired, McpServerConnectionManager.Classify(unauthorized, hasHeaders: false));
        AssertEx.Equal(McpConnectionFailureReason.Authentication, McpServerConnectionManager.Classify(unauthorized, hasHeaders: true));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Classify_Forbidden_IsForbiddenEvenWithHeaders()
    {
        var forbidden = new HttpRequestException("403", inner: null, HttpStatusCode.Forbidden);

        AssertEx.Equal(McpConnectionFailureReason.Forbidden, McpServerConnectionManager.Classify(forbidden, hasHeaders: true));
        AssertEx.Equal(McpConnectionFailureReason.Forbidden, McpServerConnectionManager.Classify(forbidden, hasHeaders: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Classify_TlsFailureWrappedInHttpRequestException_IsTls()
    {
        var tls = new HttpRequestException("SSL", new AuthenticationException("The remote certificate is invalid."));

        AssertEx.Equal(McpConnectionFailureReason.Tls, McpServerConnectionManager.Classify(tls, hasHeaders: false));
        AssertEx.Equal(McpConnectionFailureReason.Tls, McpServerConnectionManager.Classify(new AuthenticationException("bare"), hasHeaders: false));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Classify_StartupException_IsServerStartupFailed()
    {
        AssertEx.Equal(McpConnectionFailureReason.ServerStartupFailed, McpServerConnectionManager.Classify(new McpServerStartupException("exited", "tail"), hasHeaders: false));
        await Task.CompletedTask;
    }

    private static async Task<McpServerConnectionStatus> RefreshWithFailureAsync(Func<Exception> failure, IAgentSandboxRuntimeProvider? sandboxProvider = null,
        Dictionary<string, string>? environment = null, SandboxSecurityProfile profile = SandboxSecurityProfile.Low)
    {
        var record = StdioRecord("Broken") with
        {
            Environment = environment ?? new Dictionary<string, string>()
        };
        var factory = new FakeMcpClientFactory();
        factory.FailFor(record.Id, failure);
        await using var manager = CreateManager(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), factory, new FakeMcpServerStore(record), sandboxProvider,
            profile: profile);

        await manager.RefreshAsync();

        var status = manager.GetStatuses().Single();
        AssertEx.False(status.Connected);
        return status;
    }

    [Test]
    public async Task RefreshAsync_WhenACollidingServerIsAddedLater_KeepsTheOriginalSlug()
    {
        // O-D5: slugs are persisted on first connect, so a later server whose Name slugifies alike takes the next suffix
        // instead of shifting the original's tool names (and every agent allow-list naming them) onto itself.
        await using var first = await InProcMcpServer.StartAsync("first",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var firstRecord = StdioRecord("Server"); // slugifies to "server"
        var store = new FakeMcpServerStore(firstRecord);
        var factory = new FakeMcpClientFactory((firstRecord.Id, first.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);

        await manager.RefreshAsync();
        AssertEx.Contains(registry.GetDescriptors().Select(static d => d.Name), "mcp__server__get_forecast");

        await using var second = await InProcMcpServer.StartAsync("second",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var secondRecord = StdioRecord("Server!");
        factory.AddClient(secondRecord.Id, second.Client);
        store.Upsert(secondRecord);

        await manager.RefreshAsync();

        var names = registry.GetDescriptors().Select(static d => d.Name).ToList();
        AssertEx.Equal("mcp__server-2__get_forecast,mcp__server__get_forecast", string.Join(",", names));
        AssertEx.Equal("server", store.SlugOf(firstRecord.Id));
        AssertEx.Equal("server-2", store.SlugOf(secondRecord.Id));
        AssertEx.Equal(expected: 1, factory.CreateCount(firstRecord.Id), "the original server keeps its live session");
    }

    [Test]
    public async Task RefreshAsync_SlugIsStableAcrossAnotherServersDisableAndEnableAndARename()
    {
        // The live-round repro: disabling "ticket-desk" re-slugged "Ticket Desk" from ticket-desk-2 to ticket-desk, so
        // mcp__ticket-desk__* silently resolved to the other server.
        await using var a1 = await InProcMcpServer.StartAsync("a1", AIFunctionFactory.Create(GetForecast, "lookup"));
        await using var a2 = await InProcMcpServer.StartAsync("a2", AIFunctionFactory.Create(GetForecast, "lookup"));
        await using var b = await InProcMcpServer.StartAsync("b", AIFunctionFactory.Create(GetForecast, "lookup"));
        var recordA = StdioRecord("ticket-desk");
        var recordB = StdioRecord("Ticket Desk");
        var store = new FakeMcpServerStore(recordA, recordB);
        var factory = new FakeMcpClientFactory((recordA.Id, a1.Client), (recordB.Id, b.Client));
        factory.AddClient(recordA.Id, a2.Client);
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);

        await manager.RefreshAsync();
        AssertEx.Equal("mcp__ticket-desk-2__lookup,mcp__ticket-desk__lookup", string.Join(",", registry.GetDescriptors().Select(static d => d.Name)));

        store.Upsert(recordA with
        {
            Enabled = false
        });
        await manager.RefreshAsync(recordA.Id);
        AssertEx.Equal("mcp__ticket-desk-2__lookup", string.Join(",", registry.GetDescriptors().Select(static d => d.Name)),
            "disabling the first server must not re-bind the second server's tool names");

        store.Upsert(recordA with
        {
            Enabled = true
        });
        store.Upsert(recordB with
        {
            Name = "Help Desk"
        });
        await manager.RefreshAsync();
        AssertEx.Equal("mcp__ticket-desk-2__lookup,mcp__ticket-desk__lookup", string.Join(",", registry.GetDescriptors().Select(static d => d.Name)),
            "re-enabling keeps the persisted slug and a rename never recomputes one");
        AssertEx.Equal(expected: 1, factory.CreateCount(recordB.Id), "the renamed server keeps its session: a rename is not a config change");
    }

    [Test]
    public async Task RefreshAsync_FirstSlugAssignment_ReproducesTheEnabledOrderSuffixes()
    {
        // Upgraded nodes have no persisted slugs. The first assignment must hand out exactly the suffixes the old per-refresh
        // computation did over the ENABLED set in store order, so existing agent allow-lists keep resolving; a disabled row claims none.
        await using var first = await InProcMcpServer.StartAsync("first", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        await using var second = await InProcMcpServer.StartAsync("second", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var disabled = StdioRecord("Server") with
        {
            Enabled = false
        };
        var enabledFirst = StdioRecord("Server!");
        var enabledSecond = StdioRecord("Server?");
        var store = new FakeMcpServerStore(disabled, enabledFirst, enabledSecond);
        var factory = new FakeMcpClientFactory((enabledFirst.Id, first.Client), (enabledSecond.Id, second.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);

        await manager.RefreshAsync();

        AssertEx.Equal("server", store.SlugOf(enabledFirst.Id));
        AssertEx.Equal("server-2", store.SlugOf(enabledSecond.Id));
        AssertEx.Null(store.SlugOf(disabled.Id));
    }

    [Test]
    public async Task RefreshAsync_ById_ReconnectsOnlyThatServer()
    {
        // O-D11: a registration change reconnects the changed server, never every other server.
        await using var a1 = await InProcMcpServer.StartAsync("a1", AIFunctionFactory.Create(GetForecast, "tool_a"));
        await using var a2 = await InProcMcpServer.StartAsync("a2", AIFunctionFactory.Create(GetForecast, "tool_a"));
        await using var b = await InProcMcpServer.StartAsync("b", AIFunctionFactory.Create(GetForecast, "tool_b"));
        var recordA = StdioRecord("Alpha");
        var recordB = StdioRecord("Bravo");
        var store = new FakeMcpServerStore(recordA, recordB);
        var factory = new FakeMcpClientFactory((recordA.Id, a1.Client), (recordB.Id, b.Client));
        factory.AddClient(recordA.Id, a2.Client);
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);
        await manager.RefreshAsync();

        store.Upsert(recordA with
        {
            Version = 2
        });
        await manager.RefreshAsync(recordA.Id);

        AssertEx.Equal(expected: 2, factory.CreateCount(recordA.Id));
        AssertEx.Equal(expected: 1, factory.CreateCount(recordB.Id));
        AssertEx.Equal("mcp__alpha__tool_a,mcp__bravo__tool_b", string.Join(",", registry.GetDescriptors().Select(static d => d.Name)));
    }

    [Test]
    public async Task SessionEnds_MarksTheServerErrored_KeepsItsToolsOffered_AndACallFailsTyped()
    {
        // O-D3: a crashed server stayed "connected" and its calls failed with a bare "Error: Function failed.".
        await using var server = await InProcMcpServer.StartAsync("weather", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var factory = new FakeMcpClientFactory((record.Id, server.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();

        factory.FailFor(record.Id, static () => new IOException("Simulated restart failure."));
        await server.StopServerAsync();

        await AssertEx.EventuallyAsync(() => manager.GetStatuses().Single().FailureReason == McpConnectionFailureReason.ServerExited,
            TimeSpan.FromSeconds(10), "the dead session must put the server into the error state");
        var status = manager.GetStatuses().Single();
        AssertEx.False(status.Connected);
        AssertEx.Equal("The MCP server process exited.", status.LastError);

        var executable = Resolve(registry, "mcp__weather__get_forecast");
        var result = ResultText(await executable.InvokeAsync(Arguments()));

        AssertEx.Equal(
            "[tool error: server_unavailable] MCP server 'Weather' is not connected: The connection to the MCP server failed or closed. The next call retries; use Reconnect on the MCP page if it keeps failing.",
            result);
        AssertEx.Equal(expected: 2, factory.CreateCount(record.Id), "the call reconnects exactly once before failing");
    }

    [Test]
    public async Task SessionEnds_TheNextCallReconnectsOnceAndSucceeds()
    {
        await using var first = await InProcMcpServer.StartAsync("weather", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        await using var restarted = await InProcMcpServer.StartAsync("weather-restarted", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var factory = new FakeMcpClientFactory((record.Id, first.Client));
        factory.AddClient(record.Id, restarted.Client);
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();

        await first.StopServerAsync();
        await AssertEx.EventuallyAsync(() => !manager.GetStatuses().Single().Connected, TimeSpan.FromSeconds(10));

        var executable = Resolve(registry, "mcp__weather__get_forecast");
        var result = ResultText(await executable.InvokeAsync(Arguments()));

        AssertEx.True(result.Contains("Sunny in Paris.", StringComparison.Ordinal), $"the reconnected session must serve the call (got: {result})");
        AssertEx.True(manager.GetStatuses().Single().Connected, "a successful reconnect clears the error state");
        AssertEx.Equal(expected: 2, factory.CreateCount(record.Id));
    }

    [Test]
    public async Task Invoke_AfterTwoCallTimeouts_AbandonsAndReconnects()
    {
        // A wedged-but-alive server never faults the transport, so only the per-call timeouts can tell the node to recycle it.
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter");
        var factory = new FakeMcpClientFactory();
        factory.AddClient(record.Id, await pool.StartCounterAsync(wedged));
        factory.AddClient(record.Id, await pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record), mcpOptions: new McpOptions
        {
            ConnectTimeoutSeconds = 30,
            // real-timer: the per-call deadline is a CancellationTokenSource.CancelAfter with no TimeProvider seam.
            ToolCallTimeoutSeconds = 1
        });
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        try
        {
            for (var call = 0; call < McpServerConnectionManager.ConsecutiveTimeoutsBeforeAbandon; call++)
            {
                var timedOut = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
                AssertEx.True(timedOut.Contains(ToolFailureText.TimeoutCode, StringComparison.Ordinal), $"call {call + 1} must time out (got: {timedOut})");
            }

            AssertEx.Equal(expected: 1, factory.CreateCount(record.Id), "the timeouts alone never reconnect");
            var result = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));

            AssertEx.True(result.Contains("count=1", StringComparison.Ordinal), $"the next call must reach a fresh session (got: {result})");
            AssertEx.Equal(expected: 2, factory.CreateCount(record.Id));
        }
        finally
        {
            wedged.TrySetResult();
        }
    }

    [Test]
    public async Task Invoke_LateTimeoutFromARetiredSession_DoesNotEvictTheCurrentOne()
    {
        // The current session has one real timeout on it; a late timeout from a call that ran on a session since retired
        // (its token is unknown to every current session) must not count as the second strike.
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter");
        var factory = new FakeMcpClientFactory();
        factory.AddClient(record.Id, await pool.StartCounterAsync(wedged));
        factory.AddClient(record.Id, await pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record), mcpOptions: new McpOptions
        {
            ConnectTimeoutSeconds = 30,
            // real-timer: the per-call deadline is a CancellationTokenSource.CancelAfter with no TimeProvider seam.
            ToolCallTimeoutSeconds = 1
        });
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        try
        {
            var first = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
            AssertEx.True(first.Contains(ToolFailureText.TimeoutCode, StringComparison.Ordinal), $"the first call must time out (got: {first})");
            using var retiredCall = new CancellationTokenSource();
            await retiredCall.CancelAsync();

            await manager.OnToolCallTimedOutAsync(record.Id, retiredCall.Token);

            AssertEx.True(manager.GetStatuses().Single().Connected, "a late timeout from another session must not abandon this one");
            AssertEx.Equal(expected: 1, factory.CreateCount(record.Id));
            var second = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
            AssertEx.True(second.Contains(ToolFailureText.TimeoutCode, StringComparison.Ordinal), $"the call still reaches the wedged session (got: {second})");
            AssertEx.Equal(expected: 1, factory.CreateCount(record.Id), "its own second timeout is what abandons it, on the next call");
            var third = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
            AssertEx.True(third.Contains("count=1", StringComparison.Ordinal), $"the next call must reach a fresh session (got: {third})");
        }
        finally
        {
            wedged.TrySetResult();
        }
    }

    [Test]
    public async Task ToolListChanged_RelistsAndRepublishesAfterTheDebounce()
    {
        // O-D9: a server that adds a tool announces it with tools/list_changed; the offer must follow without a toggle.
        // Pinned to an initialize-era revision: that is where a server sends list_changed unsolicited, as the live round's servers did.
        var clock = new ManualTimeProvider();
        await using var server = await InProcMcpServer.StartAsync("weather", "2025-11-25", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), new FakeMcpServerStore(record), timeProvider: clock);
        await manager.RefreshAsync();

        server.AddTool(AIFunctionFactory.Create(GetForecast, "get_warnings"));

        // The debounce waits on the manual clock, so move it along until the re-list has landed.
        await AssertEx.EventuallyAsync(() =>
        {
            clock.Advance(McpServerConnectionManager.ToolListChangedDebounce);
            return registry.TryResolve("mcp__weather__get_warnings", out _);
        }, TimeSpan.FromSeconds(10), "the new tool must be offered after tools/list_changed");
        AssertEx.Equal(expected: 2, manager.GetStatuses().Single().ToolCount);
    }

    [Test]
    public async Task PerConversationScope_GivesEachConversationItsOwnSession_AndCallsWithoutOneShareTheServerSession()
    {
        // O-D1: one shared session let a stateful server's per-session memory from conversation A block conversation B.
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");
        var next = executable;

        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        string a1, a2, b1, shared;
        using (AgentRunConversationContext.BeginScope(conversationA))
        {
            a1 = ResultText(await next.InvokeAsync(new AIFunctionArguments()));
            a2 = ResultText(await next.InvokeAsync(new AIFunctionArguments()));
        }

        using (AgentRunConversationContext.BeginScope(conversationB))
        {
            b1 = ResultText(await next.InvokeAsync(new AIFunctionArguments()));
        }

        shared = ResultText(await next.InvokeAsync(new AIFunctionArguments()));

        AssertEx.True(a1.Contains("count=1", StringComparison.Ordinal) && a2.Contains("count=2", StringComparison.Ordinal), $"conversation A keeps its own session ({a1}, {a2})");
        AssertEx.True(b1.Contains("count=1", StringComparison.Ordinal), $"conversation B starts fresh ({b1})");
        AssertEx.True(shared.Contains("count=1", StringComparison.Ordinal), $"a call with no conversation uses the shared session ({shared})");
        AssertEx.Equal(expected: 2, manager.CountConversationSessions(record.Id));
        // Each session is created under its own key (the shared one under none), which is what gives a Sandboxed server a jail per
        // session; the generation suffix keeps a replacement apart from a retired session that is still being torn down.
        AssertEx.Equal($"<shared>,{conversationA:N}-1,{conversationB:N}-2", string.Join(",", factory.SessionKeys.Select(static key => key ?? "<shared>")));
    }

    [Test]
    public async Task PerConversationScope_AReplacementSessionNeverReusesARetiredSessionsKey()
    {
        // Codex review 2026-09-30: the idle sweep unmapped a session before its disposal finished, so a conversation resuming
        // in that window got a replacement under the SAME key (same jail and execution id), which the old kill then destroyed.
        var clock = new ManualTimeProvider();
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record), timeProvider: clock);
        await manager.RefreshAsync();
        var conversation = Guid.NewGuid();
        var executable = Resolve(registry, "mcp__counter__next");

        using (AgentRunConversationContext.BeginScope(conversation))
        {
            _ = await executable.InvokeAsync(new AIFunctionArguments());
        }

        clock.Advance(McpServerConnectionManager.ConversationSessionIdleTimeout + TimeSpan.FromMinutes(1));
        await AssertEx.EventuallyAsync(() => manager.CountConversationSessions(record.Id) == 0, TimeSpan.FromSeconds(10), "the idle sweep retires the session");

        using (AgentRunConversationContext.BeginScope(conversation))
        {
            _ = await executable.InvokeAsync(new AIFunctionArguments());
        }

        var keys = factory.SessionKeys.Where(static key => key is not null).ToList();
        AssertEx.Equal(expected: 2, keys.Count);
        AssertEx.True(keys[0] != keys[1], $"the replacement must not reuse the retired key ({keys[0]} vs {keys[1]})");
        AssertEx.True(keys.All(key => key!.StartsWith(conversation.ToString("N"), StringComparison.Ordinal)), "both keys belong to the conversation");
    }

    [Test]
    public async Task PerConversationScope_SharedRegistrationIgnoresTheConversation()
    {
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter");
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            _ = await executable.InvokeAsync(new AIFunctionArguments());
        }

        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            var second = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
            AssertEx.True(second.Contains("count=2", StringComparison.Ordinal), $"Shared is the default and keeps one session ({second})");
        }

        AssertEx.Equal(expected: 0, manager.CountConversationSessions(record.Id));
    }

    [Test]
    public async Task PerConversationScope_TheIdleSweepNeverRetiresASessionWithACallInFlight()
    {
        // Codex review 2026-09-30: LastUsedUtc moved only on acquire, so a call longer than the idle window lost its session mid-call.
        var clock = new ManualTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync(gate));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record), timeProvider: clock);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        Task<object?> call;
        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            call = executable.InvokeAsync(new AIFunctionArguments()).AsTask();
        }

        await AssertEx.EventuallyAsync(() => manager.CountConversationSessions(record.Id) == 1, TimeSpan.FromSeconds(10), "the call opened a session");
        clock.Advance(McpServerConnectionManager.ConversationSessionIdleTimeout + TimeSpan.FromMinutes(1));
        AssertEx.Equal(expected: 1, manager.CountConversationSessions(record.Id), "a session with a call in flight survives the sweep");

        gate.SetResult();
        AssertEx.True(ResultText(await call).Contains("count=1", StringComparison.Ordinal), "the call completes on its own session");

        clock.Advance(McpServerConnectionManager.ConversationSessionIdleTimeout + TimeSpan.FromMinutes(1));
        await AssertEx.EventuallyAsync(() => manager.CountConversationSessions(record.Id) == 0, TimeSpan.FromSeconds(10),
            "once the call ended the idle window runs from its end");
    }

    [Test]
    public async Task PerConversationScope_AnIdleSessionIsDisposedAfterFifteenMinutes()
    {
        var clock = new ManualTimeProvider();
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record), timeProvider: clock);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            _ = await executable.InvokeAsync(new AIFunctionArguments());
        }

        AssertEx.Equal(expected: 1, manager.CountConversationSessions(record.Id));

        clock.Advance(McpServerConnectionManager.ConversationSessionIdleTimeout - TimeSpan.FromMinutes(2));
        AssertEx.Equal(expected: 1, manager.CountConversationSessions(record.Id), "a session used within the window stays");

        clock.Advance(TimeSpan.FromMinutes(3));
        await AssertEx.EventuallyAsync(() => manager.CountConversationSessions(record.Id) == 0, TimeSpan.FromSeconds(10),
            "the idle sweep must dispose a per-conversation session idle for 15 minutes");
    }

    [Test]
    public async Task PerConversationScope_AStdioServerCapsConversationSessions_AndTheNextFailsTyped()
    {
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");

        for (var i = 0; i < McpServerConnectionManager.MaxConversationSessionsPerStdioServer; i++)
        {
            using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
            {
                var ok = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
                AssertEx.True(ok.Contains("count=1", StringComparison.Ordinal), ok);
            }
        }

        string refused;
        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            refused = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
        }

        AssertEx.True(refused.StartsWith("[tool error: server_unavailable] MCP server 'Counter' already runs 4 per-conversation sessions", StringComparison.Ordinal), refused);
        AssertEx.Equal(expected: 4, manager.CountConversationSessions(record.Id), "no silent fallback to the shared session");
    }

    [Test]
    public async Task RefreshAsync_OrdersToolsDeterministically_RegardlessOfServerOrder()
    {
        await using var alpha = await InProcMcpServer.StartAsync("alpha",
            AIFunctionFactory.Create(GetForecast, "tool_b"));
        await using var bravo = await InProcMcpServer.StartAsync("bravo",
            AIFunctionFactory.Create(GetForecast, "tool_a"));
        var alphaRecord = StdioRecord("Alpha");
        var bravoRecord = StdioRecord("Bravo");

        var factory = new FakeMcpClientFactory((alphaRecord.Id, alpha.Client), (bravoRecord.Id, bravo.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, alphaRecord, bravoRecord);

        await manager.RefreshAsync();

        var names = registry.GetDescriptors().Select(static d => d.Name).ToList();
        var actualOrder = string.Join(",", names);
        var sortedOrder = string.Join(",", names.OrderBy(static n => n, StringComparer.Ordinal));
        AssertEx.Equal(sortedOrder, actualOrder);
        AssertEx.Equal("mcp__alpha__tool_b,mcp__bravo__tool_a", actualOrder);
    }

    [Test]
    public async Task RefreshAsync_AfterServerRemoved_DropsItsToolsAndDisposesClient()
    {
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var store = new FakeMcpServerStore(record);
        var factory = new FakeMcpClientFactory((record.Id, server.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);

        await manager.RefreshAsync();
        AssertEx.Equal(expected: 1, registry.GetDescriptors().Count);

        // Remove the server from the enabled set; a refresh must drop its tools and dispose its client.
        store.SetEnabled();
        await manager.RefreshAsync();

        AssertEx.Equal(expected: 0, registry.GetDescriptors().Count);
        AssertEx.Equal(expected: 0, manager.GetStatuses().Count);
    }

    [Test]
    public async Task DisposeAsync_TearsDownClientsAndBlocksFurtherRefresh()
    {
        await using var server = await InProcMcpServer.StartAsync("weather",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        var manager = CreateManager(registry, new FakeMcpClientFactory((record.Id, server.Client)), record);

        await manager.RefreshAsync();
        AssertEx.Equal(expected: 1, registry.GetDescriptors().Count);

        // Dispose must complete (it disposes the client the manager connected) and must not hang.
        await manager.DisposeAsync();

        // A further refresh on the disposed manager is rejected, proving the disposed state. (We do not drive a request
        // over the now-closed client, which would hang with no client-side timeout — server.DisposeAsync below simply
        // confirms the already-disposed client tears down cleanly.)
        await AssertThrowsObjectDisposedAsync(() => manager.RefreshAsync());

        // A second dispose is safe (idempotent).
        await manager.DisposeAsync();
    }

    [Test]
    public async Task RefreshAsync_WhenTheCallerCancelsAfterTheClientConnected_DisposesTheClient()
    {
        // A cancel between connect and list was not caught, so the connected client — a child process or sandbox jail — was orphaned.
        await using var server = await InProcMcpServer.StartAsync("weather", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        using var caller = new CancellationTokenSource();
        var factory = new FakeMcpClientFactory((record.Id, server.Client))
        {
            AfterCreate = caller.Cancel
        };
        await using var manager = CreateManager(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), factory, record);

        await AssertThrowsCancelledAsync(() => manager.RefreshAsync(caller.Token));

        await AssertEx.EventuallyAsync(() => server.Client.Completion.IsCompleted, TimeSpan.FromSeconds(10),
            "the client connected before the cancel must be disposed, not orphaned");
    }

    [Test]
    public async Task ReconnectAsync_OnALiveServer_ClosesEverySessionAndReconnectsThePrimary()
    {
        // The UI's Reconnect went through RefreshAsync(id), which returns early for a live server, so a connected-but-stuck server
        // was never reconnected and its conversation sessions were never touched.
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter") with
        {
            SessionScope = McpSessionScope.PerConversation
        };
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, record);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");
        _ = await executable.InvokeAsync(new AIFunctionArguments());
        using (AgentRunConversationContext.BeginScope(Guid.NewGuid()))
        {
            _ = await executable.InvokeAsync(new AIFunctionArguments());
        }

        await manager.RefreshAsync(record.Id);
        AssertEx.Equal(expected: 2, factory.CreateCount(record.Id), "a plain refresh keeps a live server's sessions");

        await manager.ReconnectAsync(record.Id);

        AssertEx.Equal(expected: 3, factory.CreateCount(record.Id), "Reconnect re-opens the shared session even though it was live");
        AssertEx.Equal(expected: 0, manager.CountConversationSessions(record.Id), "Reconnect closes the conversation sessions too");
        AssertEx.True(manager.GetStatuses().Single().Connected);
        var fresh = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));
        AssertEx.True(fresh.Contains("count=1", StringComparison.Ordinal), $"the call is served by the new shared session ({fresh})");
    }

    [Test]
    public async Task Classify_NotFoundMidSession_IsSessionLost_AtConnectItIsTransport()
    {
        var notFound = new HttpRequestException("404", inner: null, HttpStatusCode.NotFound);

        AssertEx.Equal(McpConnectionFailureReason.SessionLost, McpServerConnectionManager.Classify(notFound, hasHeaders: false, inSession: true));
        AssertEx.Equal(McpConnectionFailureReason.Transport, McpServerConnectionManager.Classify(notFound, hasHeaders: false));
        await Task.CompletedTask;
    }

    private static async Task AssertThrowsCancelledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new AssertionException("Expected the caller's cancellation to propagate.");
    }

    private static async Task AssertThrowsObjectDisposedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new AssertionException("Expected ObjectDisposedException from the disposed manager.");
    }

    [Description("Returns the weather forecast for a city.")]
    private static string GetForecast(string city)
    {
        return $"Sunny in {city}.";
    }

    [Test]
    public async Task RevokeAsync_WithdrawsTheServerAtOnce_EvenWhileAnotherServersRefreshHoldsTheGate()
    {
        // Codex review: disable and delete returned before the detached refresh removed the server, so calls kept reaching it.
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter");
        var other = StdioRecord("Other");
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, () => pool.StartCounterAsync());
        factory.SpawnFor(other.Id, async () =>
        {
            await blocker.Task;
            throw new IOException("refused");
        });
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        var store = new FakeMcpServerStore(record);
        await using var manager = CreateManager(registry, factory, store);
        await manager.RefreshAsync();
        var executable = Resolve(registry, "mcp__counter__next");
        store.Upsert(other);
        var blockedRefresh = manager.RefreshAsync(other.Id);
        await AssertEx.EventuallyAsync(() => factory.CreateCount(other.Id) == 1, TestBudgets.Contended, "the other server's connect holds the refresh gate");

        await AssertEx.CompletesAsync(manager.RevokeAsync(record.Id), TestBudgets.Contended, "a revoke must not wait for the refresh gate");
        var result = ResultText(await executable.InvokeAsync(new AIFunctionArguments()));

        AssertEx.Contains(result, McpServerConnectionManager.ServerUnavailableCode);
        AssertEx.False(blockedRefresh.IsCompleted, "the other refresh is still holding the gate");
        blocker.SetResult();
        await blockedRefresh;
    }

    [Test]
    public async Task ReconnectAsync_OfAFailedServer_ReportsConnectingBeforeItTakesTheGate()
    {
        // Codex review: the old error stayed until the gate was taken, and the SPA, polling only while connecting, stopped there.
        await using var pool = new ServerPool();
        var record = StdioRecord("Counter");
        var park = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var factory = new FakeMcpClientFactory();
        factory.SpawnFor(record.Id, async () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("refused");
            }

            await park.Task;
            return await pool.StartCounterAsync();
        });
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, new FakeMcpServerStore(record));
        await manager.RefreshAsync();
        AssertEx.NotNull(manager.GetStatuses().Single().LastError, "the first connect failed");

        var reconnect = manager.ReconnectAsync(record.Id);
        var pending = manager.GetStatuses().Single();

        AssertEx.False(pending.Connected);
        AssertEx.Null(pending.LastError, "a status read right after Reconnect must read as connecting, not as the old error");
        park.SetResult();
        await reconnect;
        AssertEx.True(manager.GetStatuses().Single().Connected, "the reconnect completed");
    }

    [Test]
    public async Task ReconnectAsync_OfAServerWhosePrimaryExited_ReadsConnectingWhileQueuedBehindTheGate()
    {
        // Codex review: the exited primary stays on the entry, so clearing the error alone read "connected" with the old tools while the
        // reconnect was still queued, and the SPA stopped polling before it learned the outcome.
        await using var first = await InProcMcpServer.StartAsync("weather", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        await using var second = await InProcMcpServer.StartAsync("weather", AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var record = StdioRecord("Weather");
        var other = StdioRecord("Other");
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeMcpClientFactory();
        factory.AddClient(record.Id, first.Client);
        factory.AddClient(record.Id, second.Client);
        factory.SpawnFor(other.Id, async () =>
        {
            await blocker.Task;
            throw new IOException("refused");
        });
        var store = new FakeMcpServerStore(record);
        await using var manager = CreateManager(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), factory, store);
        await manager.RefreshAsync();
        await first.StopServerAsync();
        await AssertEx.EventuallyAsync(() => manager.GetStatuses().Single().FailureReason == McpConnectionFailureReason.ServerExited,
            TestBudgets.Contended, "the exited primary put the server into the error state");
        store.Upsert(other);
        var blockedRefresh = manager.RefreshAsync(other.Id);
        await AssertEx.EventuallyAsync(() => factory.CreateCount(other.Id) == 1, TestBudgets.Contended, "the other server's connect holds the refresh gate");

        var reconnect = manager.ReconnectAsync(record.Id);
        var pending = manager.GetStatuses().Single(status => status.ServerId == record.Id);

        AssertEx.False(pending.Connected, "a reconnect queued behind the gate must not read as connected off the exited primary");
        AssertEx.Null(pending.LastError, "nor as the old error: the tools view reads this as connecting");
        AssertEx.False(reconnect.IsCompleted, "the reconnect is still queued behind the other refresh");
        blocker.SetResult();
        await blockedRefresh;
        await reconnect;
        AssertEx.True(manager.GetStatuses().Single(status => status.ServerId == record.Id).Connected, "the reconnect completed on the next client");
    }

    private static AIFunction Resolve(McpToolRegistry registry, string name)
    {
        AssertEx.True(registry.TryResolve(name, out var tool), $"{name} must stay offered");
        return (AIFunction)tool!;
    }

    private static AIFunctionArguments Arguments()
    {
        return new AIFunctionArguments
        {
            ["city"] = "Paris"
        };
    }

    // A tool result is either the typed failure string or the SDK's AIContent projection; both read as text here.
    private static string ResultText(object? result)
    {
        return result as string ?? JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
    }

    private static McpServerRecord StdioRecord(string name)
    {
        return new McpServerRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = null,
            TransportKind = McpTransportKind.Stdio,
            Command = "noop",
            Arguments = [],
            WorkingDirectory = null,
            Environment = new Dictionary<string, string>(),
            Url = null,
            TrustTier = McpTrustTier.PrivilegedHost,
            Enabled = true,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0
        };
    }

    private static McpServerConnectionManager CreateManager(McpToolRegistry registry, FakeMcpClientFactory factory, params McpServerRecord[] enabled)
    {
        var store = new FakeMcpServerStore(enabled);
        return CreateManager(registry, factory, store);
    }

    private static McpServerConnectionManager CreateManager(McpToolRegistry registry, FakeMcpClientFactory factory, IMcpServerStore store, IAgentSandboxRuntimeProvider? sandboxProvider = null,
        TimeProvider? timeProvider = null, McpOptions? mcpOptions = null, SandboxSecurityProfile profile = SandboxSecurityProfile.Low)
    {
        return new McpServerConnectionManager(BuildScopeFactory(store), registry, factory, sandboxProvider ?? new FakeSandboxRuntimeProvider(TimeProvider.System),
            new StubNodeRuntimeSettings().WithSandboxSecurityProfile(profile).Build(),
            mcpOptions is null ? Options() : Microsoft.Extensions.Options.Options.Create(mcpOptions),
            Microsoft.Extensions.Options.Options.Create(new AgentToolPipelineOptions()), timeProvider ?? TimeProvider.System, NullLogger<McpServerConnectionManager>.Instance);
    }

    // The manager resolves the (Scoped) store through a scope, so the test wraps the fake store in a real service
    // provider that hands out the same instance per scope. This mirrors the production captive-dependency fix.
    private static IServiceScopeFactory BuildScopeFactory(IMcpServerStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IOptions<McpOptions> Options()
    {
        return Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            ConnectTimeoutSeconds = 30
        });
    }

    /// <summary>In-memory registrations in store order (oldest first), with the slug assignment the manager persists.</summary>
    private sealed class FakeMcpServerStore : IMcpServerStore
    {
        private readonly Lock _gate = new();
        private List<McpServerRecord> _records;

        public FakeMcpServerStore(params McpServerRecord[] records)
        {
            _records = [.. records];
        }

        public Task<IReadOnlyList<McpServerRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<McpServerRecord>>([.. _records.Where(static r => r.Enabled)]);
            }
        }

        public Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<McpServerRecord>>([.. _records]);
            }
        }

        public Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.FirstOrDefault(r => r.Id == id));
            }
        }

        public Task<bool> AssignSlugAsync(Guid id, string slug, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var index = _records.FindIndex(r => r.Id == id);
                if (index < 0 || _records[index].Slug is not null)
                {
                    return Task.FromResult(false);
                }

                _records[index] = _records[index] with
                {
                    Slug = slug
                };
                return Task.FromResult(true);
            }
        }

        public Task<McpServerRecord> AddAsync(McpServerInput input, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>Replaces the whole registration set with these (enabled) records.</summary>
        public void SetEnabled(params McpServerRecord[] enabled)
        {
            lock (_gate)
            {
                _records = [.. enabled];
            }
        }

        /// <summary>Adds a record, or replaces the stored one with the same id while keeping any slug already persisted.</summary>
        public void Upsert(McpServerRecord record)
        {
            lock (_gate)
            {
                var index = _records.FindIndex(r => r.Id == record.Id);
                if (index < 0)
                {
                    _records.Add(record);
                }
                else
                {
                    _records[index] = record with
                    {
                        Slug = _records[index].Slug
                    };
                }
            }
        }

        public string? SlugOf(Guid id)
        {
            lock (_gate)
            {
                return _records.Single(r => r.Id == id).Slug;
            }
        }
    }

    /// <summary>
    ///     Hands out queued clients per server (the last one repeats), or spawns a fresh one per connect, and counts connects.
    /// </summary>
    private sealed class FakeMcpClientFactory : IMcpClientFactory
    {
        private readonly Dictionary<Guid, Queue<McpClient>> _clients = [];
        private readonly Dictionary<Guid, int> _creates = [];
        private readonly Dictionary<Guid, Func<Exception>> _failures = [];
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, Func<Task<McpClient>>> _spawners = [];
        private readonly List<string?> _sessionKeys = [];

        /// <summary>Runs after a client was handed out, before the manager lists its tools.</summary>
        public Action? AfterCreate { get; set; }

        public IReadOnlyList<string?> SessionKeys
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sessionKeys];
                }
            }
        }

        public FakeMcpClientFactory(params (Guid Id, McpClient Client)[] clients)
        {
            foreach (var (id, client) in clients)
            {
                AddClient(id, client);
            }
        }

        public async Task<McpClient> CreateAsync(McpServerRecord record, string? sessionKey, CancellationToken cancellationToken)
        {
            Task<McpClient> created;
            lock (_gate)
            {
                _creates[record.Id] = _creates.GetValueOrDefault(record.Id) + 1;
                _sessionKeys.Add(sessionKey);
                if (_failures.TryGetValue(record.Id, out var exceptionFactory))
                {
                    throw exceptionFactory();
                }

                if (_spawners.TryGetValue(record.Id, out var spawn))
                {
                    created = spawn();
                }
                else
                {
                    var queue = _clients[record.Id];
                    created = Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
                }
            }

            var client = await created;
            AfterCreate?.Invoke();
            return client;
        }

        public int CreateCount(Guid id)
        {
            lock (_gate)
            {
                return _creates.GetValueOrDefault(id);
            }
        }

        /// <summary>Queues a client for the server's next connect.</summary>
        public void AddClient(Guid id, McpClient client)
        {
            lock (_gate)
            {
                if (!_clients.TryGetValue(id, out var queue))
                {
                    _clients[id] = queue = new Queue<McpClient>();
                }

                queue.Enqueue(client);
            }
        }

        public void SpawnFor(Guid id, Func<Task<McpClient>> spawn)
        {
            lock (_gate)
            {
                _spawners[id] = spawn;
            }
        }

        public void FailFor(Guid id)
        {
            FailFor(id, static () => new McpException("Simulated MCP server connect failure."));
        }

        public void FailFor(Guid id, Func<Exception> exceptionFactory)
        {
            lock (_gate)
            {
                _failures[id] = exceptionFactory;
            }
        }
    }

    /// <summary>Owns every in-process server a spawning factory starts, each with its own call counter.</summary>
    private sealed class ServerPool : IAsyncDisposable
    {
        private readonly List<InProcMcpServer> _servers = [];

        public Task<McpClient> StartCounterAsync() =>
            StartCounterAsync(gate: null);

        /// <summary>A counter whose "next" tool, when a gate is given, waits on it before answering (an in-flight call).</summary>
        public async Task<McpClient> StartCounterAsync(TaskCompletionSource? gate)
        {
            var count = 0;
            var server = await InProcMcpServer.StartAsync("counter",
                AIFunctionFactory.Create(async () =>
                {
                    if (gate is not null)
                    {
                        await gate.Task;
                    }

                    return string.Create(CultureInfo.InvariantCulture, $"count={Interlocked.Increment(ref count)}");
                }, "next"));
            lock (_servers)
            {
                _servers.Add(server);
            }

            return server.Client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var server in _servers)
            {
                await server.DisposeAsync();
            }
        }
    }
}
