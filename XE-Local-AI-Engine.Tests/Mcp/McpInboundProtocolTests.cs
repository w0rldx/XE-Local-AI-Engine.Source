namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end protocol coverage for the inbound server using the real MCP SDK Streamable HTTP transport against
///     <see cref="TestServerWebAppFactory" />. The lifecycle and workspace seams are explicit deterministic fakes, so no
///     model process, dispatcher timing, or filesystem is involved.
/// </summary>
[NotInParallel]
public sealed class McpInboundProtocolTests
{
    private const string EndpointRoute = "/api/local/v1/mcp/server";
    private const string ValidKey = "xemcp_protocol-test-key";

    private static readonly string[] ExpectedToolNames =
    [
        "cancel_agent_run",
        "get_agent_run",
        "list_agent_runs",
        "list_agents",
        "list_models",
        "list_workspaces",
        "run_agent",
        "start_agent_run"
    ];

    [Test]
    public async Task StreamableHttpSdk_WithBearer_InitializesListsExactlyEightToolsAndCallsListWorkspaces()
    {
        var coordinator = new FakeMcpAgentRunCoordinator();
        var workspaceId = Guid.NewGuid().ToString("D");
        var workspaces = new FakeSelectedFolderResolver
        {
            References = [new SelectedFolderReference(workspaceId, "engine")]
        };
        await using var factory = CreateFactory(coordinator, workspaces);
        await using var client = await CreateClientAsync(factory);

        var tools = await client.ListToolsAsync().ConfigureAwait(false);
        var result = await client.CallToolAsync("list_workspaces").ConfigureAwait(false);
        var text = GetText(result);

        AssertEx.Equal(string.Join('|', ExpectedToolNames),
            string.Join('|', tools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal)));
        AssertEx.Contains(text, workspaceId);
        AssertEx.Contains(text, "engine");
        AssertEx.False(text.Contains("path", StringComparison.OrdinalIgnoreCase), "Protocol workspace discovery must not expose path fields.");
    }

    [Test]
    public async Task StreamableHttpSdk_StartOnConnectionA_AllowsGetListAndCancelOnConnectionB()
    {
        var coordinator = new FakeMcpAgentRunCoordinator();
        var requestId = Guid.NewGuid();
        await using var factory = CreateFactory(coordinator, new FakeSelectedFolderResolver());

        await using (var connectionA = await CreateClientAsync(factory))
        {
            var start = await connectionA.CallToolAsync("start_agent_run",
                new Dictionary<string, object?>
                {
                    ["request_id"] = requestId.ToString("D"),
                    ["task"] = "inspect",
                    ["model"] = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M"
                }).ConfigureAwait(false);

            AssertEx.Contains(GetText(start), "accepted");
        }

        await using var connectionB = await CreateClientAsync(factory);
        var get = await connectionB.CallToolAsync("get_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            }).ConfigureAwait(false);
        var list = await connectionB.CallToolAsync("list_agent_runs").ConfigureAwait(false);
        var cancel = await connectionB.CallToolAsync("cancel_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            }).ConfigureAwait(false);

        AssertEx.Contains(GetText(get), requestId.ToString("D"));
        AssertEx.Contains(GetText(list), requestId.ToString("D"));
        AssertEx.Contains(GetText(cancel), "requested");
        AssertEx.Equal(1, coordinator.CancelCallCount);
    }

    [Test]
    public async Task StreamableHttpSdk_DisconnectingAfterAcceptance_DoesNotCancelDurableRun()
    {
        var coordinator = new FakeMcpAgentRunCoordinator();
        var requestId = Guid.NewGuid();
        await using var factory = CreateFactory(coordinator, new FakeSelectedFolderResolver());

        await using (var connectionA = await CreateClientAsync(factory))
        {
            var start = await connectionA.CallToolAsync("start_agent_run",
                new Dictionary<string, object?>
                {
                    ["request_id"] = requestId.ToString("D"),
                    ["task"] = "inspect",
                    ["model"] = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M"
                }).ConfigureAwait(false);
            AssertEx.Contains(GetText(start), "accepted");
        }

        AssertEx.Equal(0, coordinator.CancelCallCount);
        await using var connectionB = await CreateClientAsync(factory);
        var get = await connectionB.CallToolAsync("get_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            }).ConfigureAwait(false);

        AssertEx.Contains(GetText(get), "queued");
        AssertEx.Equal(0, coordinator.CancelCallCount);
    }

    private static TestServerWebAppFactory CreateFactory(FakeMcpAgentRunCoordinator coordinator,
        FakeSelectedFolderResolver workspaces)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IMcpServerApiKeyService>();
                services.AddSingleton<IMcpServerApiKeyService>(new FakeMcpServerApiKeyService(ValidKey));
                services.RemoveAll<IMcpAgentRunCoordinator>();
                services.AddSingleton<IMcpAgentRunCoordinator>(coordinator);
                services.RemoveAll<ISelectedFolderResolver>();
                services.AddSingleton<ISelectedFolderResolver>(workspaces);
            }
        };
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "McpClient takes ownership of the transport on success; the exceptional path disposes it explicitly.")]
    private static async Task<McpClient> CreateClientAsync(TestServerWebAppFactory factory)
    {
        var httpClient = factory.CreateClient();
        var endpoint = new Uri(httpClient.BaseAddress!, EndpointRoute);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = $"xe-engine-test-{Guid.NewGuid():N}",
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer {ValidKey}"
                }
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await McpClient.CreateAsync(transport,
                clientOptions: null,
                NullLoggerFactory.Instance,
                deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string GetText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

    private sealed class FakeMcpAgentRunCoordinator : IMcpAgentRunCoordinator
    {
        private readonly ConcurrentDictionary<Guid, McpAgentRunView> _runs = new();
        private int _cancelCallCount;

        public int CancelCallCount => Volatile.Read(ref _cancelCallCount);

        public Task<McpAgentRunCancelResult> CancelAsync(Guid requestId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _cancelCallCount);
            if (!_runs.TryGetValue(requestId, out var current))
            {
                return Task.FromResult(new McpAgentRunCancelResult(McpAgentRunCancelKind.NotFound, null, "Run not found."));
            }

            var cancelled = current with
            {
                Status = McpAgentRunStatus.Cancelled,
                Version = current.Version + 1,
                StopReason = McpAgentRunStopReason.UserCancellation,
                CompletedAtUtc = 30,
                PayloadExpiresAtUtc = 86_400_030,
                DisplayMessage = "Cancellation requested."
            };
            _runs[requestId] = cancelled;
            return Task.FromResult(new McpAgentRunCancelResult(McpAgentRunCancelKind.Requested,
                cancelled,
                "Cancellation requested."));
        }

        public Task<McpAgentRunView?> GetAsync(Guid requestId, CancellationToken cancellationToken)
        {
            _runs.TryGetValue(requestId, out var run);
            return Task.FromResult(run);
        }

        public Task<IReadOnlyList<McpAgentRunView>> ListAsync(int? limit,
            McpAgentRunStatus? status,
            CancellationToken cancellationToken)
        {
            var results = _runs.Values.Where(run => status is null || run.Status == status)
                               .OrderByDescending(static run => run.CreatedAtUtc)
                               .Take(limit ?? 20)
                               .ToArray();
            return Task.FromResult<IReadOnlyList<McpAgentRunView>>(results);
        }

        public Task<McpAgentRunStartResult> StartAsync(McpAgentRunStartRequest request, CancellationToken cancellationToken)
        {
            var run = new McpAgentRunView(request.RequestId,
                McpAgentRunStatus.Queued,
                Version: 0,
                McpAgentRunStopReason.None,
                request.Binding.ModelId ?? request.Binding.ModelOverrideId,
                AgentDefinitionId: null,
                request.WorkspaceId,
                Result: null,
                DisplayMessage: "Accepted for background execution.",
                FailureCode: null,
                CreatedAtUtc: 10,
                ClaimedAtUtc: null,
                CompletedAtUtc: null,
                PayloadExpiresAtUtc: null,
                CompactedAtUtc: null,
                PayloadExpired: false);
            if (_runs.TryAdd(request.RequestId, run))
            {
                return Task.FromResult(new McpAgentRunStartResult(McpAgentRunStartKind.Accepted,
                    run,
                    null,
                    "Accepted for background execution."));
            }

            return Task.FromResult(new McpAgentRunStartResult(McpAgentRunStartKind.Existing,
                _runs[request.RequestId],
                null,
                "Existing run returned."));
        }
    }

    private sealed class FakeSelectedFolderResolver : ISelectedFolderResolver
    {
        public IReadOnlyList<SelectedFolderReference> References { get; init; } = [];

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(References);

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests do not mutate workspace registration.");

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests exercise only the opaque workspace list.");
    }

    private sealed class FakeMcpServerApiKeyService(string validKey) : IMcpServerApiKeyService
    {
        public Task<GeneratedMcpServerApiKey> GenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests do not rotate credentials.");

        public Task<McpServerApiKeyView?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerApiKeyView?>(new McpServerApiKeyView("xemcp_protocol", DateTimeOffset.UnixEpoch, null));

        public Task<bool> RevokeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests do not revoke credentials.");

        public Task<bool> ValidateAsync(string? presented, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(presented, validKey, StringComparison.Ordinal));
    }
}
