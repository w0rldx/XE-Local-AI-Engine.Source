namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Mcp.Server;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end protocol coverage for the inbound server using the real MCP SDK Streamable HTTP transport against
///     <see cref="TestServerWebAppFactory" />. The lifecycle and workspace seams are explicit deterministic fakes, so no
///     model process, dispatcher timing, or filesystem is involved.
/// </summary>
[Category(TestCategories.Integration)]
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
            References =
            [
                new SelectedFolderReference
                {
                    Id = workspaceId,
                    Alias = "engine"
                }
            ]
        };
        await using var factory = CreateFactory(coordinator, workspaces);
        await using var client = await CreateClientAsync(factory);

        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("list_workspaces");
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
                });

            AssertEx.Contains(GetText(start), "accepted");
        }

        await using var connectionB = await CreateClientAsync(factory);
        var get = await connectionB.CallToolAsync("get_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            });
        var list = await connectionB.CallToolAsync("list_agent_runs");
        var cancel = await connectionB.CallToolAsync("cancel_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            });

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
                });
            AssertEx.Contains(GetText(start), "accepted");
        }

        AssertEx.Equal(0, coordinator.CancelCallCount);
        await using var connectionB = await CreateClientAsync(factory);
        var get = await connectionB.CallToolAsync("get_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D")
            });

        AssertEx.Contains(GetText(get), "queued");
        AssertEx.Equal(0, coordinator.CancelCallCount);
    }

    [Test]
    public async Task StreamableHttpSdk_AgenticCredential_CapturesExplicitAuthorityAtAdmission()
    {
        var coordinator = new FakeMcpAgentRunCoordinator();
        var requestId = Guid.NewGuid();
        await using var factory = CreateFactory(coordinator,
            new FakeSelectedFolderResolver(),
            McpServerApiKeyScope.Agentic);
        await using var client = await CreateClientAsync(factory);

        _ = await client.CallToolAsync("start_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId.ToString("D"),
                ["task"] = "inspect",
                ["model"] = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M"
            });

        var admitted = AssertEx.NotNull(coordinator.LastStartRequest);
        AssertEx.True(admitted.Binding.InboundContext.IsAgentic);
        AssertEx.Equal("xemcp_protocol", admitted.Binding.InboundContext.KeyPrefix!);
        AssertEx.Equal(requestId, admitted.Binding.ExecutionRequestId);
    }

    // I-D1/I-D2: bad arguments were an opaque "An error occurred invoking" plus an ERR stack trace, and a wrong
    // spelling (model_override on run_agent, modelOverride on start_agent_run) was silently dropped.
    [Test]
    public async Task StreamableHttpSdk_BadArguments_AreTypedInvalidArgumentsNamingTheParameter()
    {
        var coordinator = new FakeMcpAgentRunCoordinator();
        await using var factory = CreateFactory(coordinator, new FakeSelectedFolderResolver());
        await using var client = await CreateClientAsync(factory);

        var unknown = await client.CallToolAsync("start_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = Guid.NewGuid().ToString("D"),
                ["task"] = "inspect",
                ["agent"] = "Coder",
                ["modelOverride"] = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M"
            });
        var wrongType = await client.CallToolAsync("get_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = 5
            });
        var missing = await client.CallToolAsync("get_agent_run");

        AssertEx.Contains(AssertTypedFailure(unknown, "invalid_arguments"), "Unknown argument 'modelOverride'");
        AssertEx.Contains(AssertTypedFailure(wrongType, "invalid_arguments"), "Argument 'request_id' must be string");
        AssertEx.Equal("Missing required argument 'request_id'.", AssertTypedFailure(missing, "invalid_arguments"));
        AssertEx.Null(coordinator.LastStartRequest, "A rejected call must never reach the tool.");
    }

    // I-D3/I-D9: every failure is isError with snake_case failure_code; success stays isError false.
    [Test]
    public async Task StreamableHttpSdk_TypedFailures_SetIsErrorWithSnakeCaseFailureCode()
    {
        await using var factory = CreateFactory(new FakeMcpAgentRunCoordinator(), new FakeSelectedFolderResolver(), McpServerApiKeyScope.Agentic);
        await using var client = await CreateClientAsync(factory);

        var badId = await client.CallToolAsync("cancel_agent_run",
            new Dictionary<string, object?>
            {
                ["request_id"] = "bad"
            });
        var adminMissing = await client.CallToolAsync("get_agent",
            new Dictionary<string, object?>
            {
                ["agent_id"] = Guid.NewGuid().ToString("D")
            });
        var ok = await client.CallToolAsync("list_workspaces");

        _ = AssertTypedFailure(badId, "invalid_request");
        AssertEx.Contains(GetText(badId), "\"status\":\"invalid_request\"");
        _ = AssertTypedFailure(adminMissing, "agent_not_found");
        AssertEx.False(GetText(adminMissing).Contains("failureCode", StringComparison.Ordinal), "Admin tools must use the snake_case failure shape.");
        AssertEx.False(ok.IsError ?? false);
    }

    // Review 2026-09-30: the filter parsed run_agent's free model output, so an answer that was JSON with a top-level failure_code
    // came back isError. Driven over the real client, so the wire shape (no internal _meta marker) is what is asserted.
    [Test]
    public async Task StreamableHttpSdk_RunAgentAnswerThatLooksLikeATypedFailure_IsNotFlaggedAsError()
    {
        const string answer = "{\"failure_code\":\"not_a_real_failure\",\"display_message\":\"the model wrote this\"}";
        var execution = Substitute.For<IMcpAgentExecutionService>();
        execution.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
                 .Returns(SpawnOutcome.Success(answer));
        await using var factory = CreateFactory(new FakeMcpAgentRunCoordinator(), new FakeSelectedFolderResolver(), execution: execution);
        await using var client = await CreateClientAsync(factory);

        var result = await client.CallToolAsync("run_agent",
            new Dictionary<string, object?>
            {
                ["task"] = "answer in JSON",
                ["model"] = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M"
            });

        AssertEx.False(result.IsError ?? false, $"free model output is a successful call: {GetText(result)}");
        AssertEx.Equal(answer, GetText(result));
        AssertEx.False(result.Meta?.ContainsKey(McpToolResults.FreeTextMetaKey) ?? false, $"the internal free-text marker must not reach the wire: {result.Meta?.ToJsonString()}");
    }

    // Returns the decoded display_message: the wire JSON escapes quotes (\u0027), so a raw substring check would test the encoder.
    private static string AssertTypedFailure(CallToolResult result, string failureCode)
    {
        AssertEx.True(result.IsError ?? false, $"Expected isError for {GetText(result)}");
        using var body = JsonDocument.Parse(GetText(result));
        AssertEx.Equal(failureCode, body.RootElement.GetProperty("failure_code").GetString()!);
        return body.RootElement.GetProperty("display_message").GetString()!;
    }

    private static TestServerWebAppFactory CreateFactory(FakeMcpAgentRunCoordinator coordinator,
        FakeSelectedFolderResolver workspaces,
        McpServerApiKeyScope scope = McpServerApiKeyScope.Delegate,
        IMcpAgentExecutionService? execution = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IMcpServerApiKeyService>();
                services.AddSingleton<IMcpServerApiKeyService>(new FakeMcpServerApiKeyService(ValidKey, scope));
                services.RemoveAll<IMcpAgentRunCoordinator>();
                services.AddSingleton<IMcpAgentRunCoordinator>(coordinator);
                services.RemoveAll<ISelectedFolderResolver>();
                services.AddSingleton<ISelectedFolderResolver>(workspaces);
                if (execution is not null)
                {
                    services.RemoveAll<IMcpAgentExecutionService>();
                    services.AddSingleton(execution);
                }
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
                deadline.Token);
        }
        catch
        {
            await transport.DisposeAsync();
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

        public McpAgentRunStartRequest? LastStartRequest { get; private set; }

        public Task<McpAgentRunCancelResult> CancelAsync(Guid requestId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _cancelCallCount);
            if (!_runs.TryGetValue(requestId, out var current))
            {
                return Task.FromResult(new McpAgentRunCancelResult
                {
                    Kind = McpAgentRunCancelKind.NotFound,
                    Run = null,
                    DisplayMessage = "Run not found."
                });
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
            return Task.FromResult(new McpAgentRunCancelResult
            {
                Kind = McpAgentRunCancelKind.Requested,
                Run = cancelled,
                DisplayMessage = "Cancellation requested."
            });
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
            LastStartRequest = request;
            var run = new McpAgentRunView
            {
                RequestId = request.RequestId,
                Status = McpAgentRunStatus.Queued,
                Version = 0,
                StopReason = McpAgentRunStopReason.None,
                ModelId = request.Binding.ModelId ?? request.Binding.ModelOverrideId,
                AgentDefinitionId = null,
                WorkspaceId = request.WorkspaceId,
                Result = null,
                DisplayMessage = "Accepted for background execution.",
                FailureCode = null,
                CreatedAtUtc = 10,
                ClaimedAtUtc = null,
                CompletedAtUtc = null,
                PayloadExpiresAtUtc = null,
                CompactedAtUtc = null,
                PayloadExpired = false
            };
            if (_runs.TryAdd(request.RequestId, run))
            {
                return Task.FromResult(new McpAgentRunStartResult
                {
                    Kind = McpAgentRunStartKind.Accepted,
                    Run = run,
                    FailureCode = null,
                    DisplayMessage = "Accepted for background execution."
                });
            }

            return Task.FromResult(new McpAgentRunStartResult
            {
                Kind = McpAgentRunStartKind.Existing,
                Run = _runs[request.RequestId],
                FailureCode = null,
                DisplayMessage = "Existing run returned."
            });
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

    private sealed class FakeMcpServerApiKeyService : IMcpServerApiKeyService
    {
        private readonly string _validKey;
        private readonly McpServerApiKeyScope _scope;

        public FakeMcpServerApiKeyService(string validKey, McpServerApiKeyScope scope)
        {
            _validKey = validKey;
            _scope = scope;
        }

        public Task<GeneratedMcpServerApiKey> GenerateAsync(McpServerApiKeyScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests do not rotate credentials.");

        public Task<McpServerApiKeyView?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerApiKeyView?>(new McpServerApiKeyView
            {
                Prefix = "xemcp_protocol",
                Scope = _scope,
                CreatedAt = DateTimeOffset.UnixEpoch,
                LastUsedAt = null
            });

        public Task<bool> RevokeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Protocol tests do not revoke credentials.");

        public Task<McpServerApiKeyValidation?> ValidateAsync(string? presented, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(presented, _validKey, StringComparison.Ordinal)
                ? new McpServerApiKeyValidation
                {
                    Scope = _scope,
                    Prefix = "xemcp_protocol"
                }
                : null);
    }
}
