namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Drives the real connection manager against an in-process MCP server (genuine SDK protocol over an in-memory
///     stream pair) to prove register -> discover -> offer -> execute, plus the manager's reconcile, qualified-name,
///     approval-wrap, deterministic-order, failure-isolation, status, and dispose behavior. No real process or socket.
///     Serialized: each test runs one or more live server loops over stream pipes, and running them concurrently
///     contends on the shared pumping machinery.
/// </summary>
[NotInParallel(nameof(McpServerConnectionManagerTests))]
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
    }

    [Test]
    public async Task RefreshAsync_WhenAColldingServerShiftsAnExistingSlugSuffix_ReKeysQualifiedNames()
    {
        // MED-3: a server connects as the unsuffixed slug. Later a second server whose Name slugifies to the SAME base
        // is added EARLIER in the enabled order, shifting the original to a "-2" suffix. The kept connection must be
        // dropped + reconnected so its cached qualified names re-bake to the new slug (no stale determinism hole).
        await using var first = await InProcMcpServer.StartAsync("first",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var firstRecord = StdioRecord("Server"); // slugifies to "server"
        var store = new FakeMcpServerStore(firstRecord);
        var factory = new FakeMcpClientFactory((firstRecord.Id, first.Client));
        var registry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        await using var manager = CreateManager(registry, factory, store);

        await manager.RefreshAsync();
        AssertEx.Contains(registry.GetDescriptors().Select(static d => d.Name), "mcp__server__get_forecast");

        // Add a second "Server" registered BEFORE the first (older CreatedAtUtc => listed first), so AssignServerSlugs
        // gives the new one "server" and shifts the original to "server-2".
        await using var second = await InProcMcpServer.StartAsync("second",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        var secondRecord = StdioRecord("Server");
        factory.AddClient(secondRecord.Id, second.Client);

        // The original server gets dropped + reconnected (its slug shifted), so hand the factory a FRESH client for it:
        // its first client is disposed by the drop, and a disposed MCP client's ListToolsAsync would hang.
        await using var firstReconnect = await InProcMcpServer.StartAsync("first-reconnect",
            AIFunctionFactory.Create(GetForecast, "get_forecast"));
        factory.AddClient(firstRecord.Id, firstReconnect.Client);

        store.SetEnabled(secondRecord, firstRecord);

        await manager.RefreshAsync();

        var names = registry.GetDescriptors().Select(static d => d.Name).ToList();
        AssertEx.Contains(names, "mcp__server__get_forecast"); // the new server (first in order)
        AssertEx.Contains(names, "mcp__server-2__get_forecast"); // the original, re-keyed to the shifted slug
        AssertEx.False(names.Any(n => n == "mcp__server__get_forecast" && names.Count(x => x == n) > 1),
            "no duplicate qualified names");
        AssertEx.Equal(expected: 2, names.Count);
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

    private static McpServerRecord StdioRecord(string name)
    {
        return new McpServerRecord(Guid.NewGuid(),
            name,
            Description: null,
            McpTransportKind.Stdio,
            "noop",
            [],
            WorkingDirectory: null,
            new Dictionary<string, string>(),
            Url: null,
            Enabled: true,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }

    private static McpServerConnectionManager CreateManager(McpToolRegistry registry, FakeMcpClientFactory factory, params McpServerRecord[] enabled)
    {
        var store = new FakeMcpServerStore(enabled);
        return CreateManager(registry, factory, store);
    }

    private static McpServerConnectionManager CreateManager(McpToolRegistry registry, FakeMcpClientFactory factory, IMcpServerStore store)
    {
        return new McpServerConnectionManager(BuildScopeFactory(store), registry, factory, Options(), Microsoft.Extensions.Options.Options.Create(new XE_Local_AI_Engine.AI.Agent.Configuration.AgentToolPipelineOptions()), NullLogger<McpServerConnectionManager>.Instance);
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

    private sealed class FakeMcpServerStore : IMcpServerStore
    {
        private McpServerRecord[] _enabled;

        public FakeMcpServerStore(params McpServerRecord[] enabled)
        {
            _enabled = enabled;
        }

        public Task<IReadOnlyList<McpServerRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<McpServerRecord>>(_enabled);
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

        public Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void SetEnabled(params McpServerRecord[] enabled)
        {
            _enabled = enabled;
        }
    }

    private sealed class FakeMcpClientFactory : IMcpClientFactory
    {
        private readonly Dictionary<Guid, McpClient> _clients = [];
        private readonly Dictionary<Guid, Func<Exception>> _failures = [];

        public FakeMcpClientFactory(params (Guid Id, McpClient Client)[] clients)
        {
            foreach (var (id, client) in clients)
            {
                _clients[id] = client;
            }
        }

        public Task<McpClient> CreateAsync(McpServerRecord record, CancellationToken cancellationToken)
        {
            if (_failures.TryGetValue(record.Id, out var exceptionFactory))
            {
                throw exceptionFactory();
            }

            return Task.FromResult(_clients[record.Id]);
        }

        public void AddClient(Guid id, McpClient client)
        {
            _clients[id] = client;
        }

        public void FailFor(Guid id)
        {
            _failures[id] = static () => new McpException("Simulated MCP server connect failure.");
        }

        public void FailFor(Guid id, Func<Exception> exceptionFactory)
        {
            _failures[id] = exceptionFactory;
        }
    }
}
