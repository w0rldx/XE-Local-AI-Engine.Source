namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Mcp.Server;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeAgentMcpToolsTests
{
    private const string Agent = "coder";
    private const string Model = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M";

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
    public void ToolSurface_AdvertisesExactlyTheEightReadOnlyDelegationTools()
    {
        var names = typeof(NodeAgentMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                             .Select(static method => (Method: method,
                                                 Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
                                             .Where(static item => item.Attribute is not null)
                                             .Select(static item => item.Attribute!.Name ?? item.Method.Name)
                                             .OrderBy(static name => name, StringComparer.Ordinal)
                                             .ToArray();

        AssertEx.Equal(string.Join('|', ExpectedToolNames), string.Join('|', names));
        var authorization = AssertEx.NotNull(typeof(NodeAgentMcpTools).GetCustomAttribute<AuthorizeAttribute>());
        AssertEx.Equal(NodeAuthorizationPolicies.McpServer, authorization.Policy!);
    }

    [Test]
    public void ToolSurface_DoesNotAdvertiseFilesystemMutationTools()
    {
        var names = typeof(NodeAgentMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                             .Select(static method => (Method: method,
                                                 Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
                                             .Where(static item => item.Attribute is not null)
                                             .Select(static item => item.Attribute!.Name ?? item.Method.Name)
                                             .ToArray();

        AssertEx.False(names.Any(static name => name.Contains("write", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("delete", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("execute", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("shell", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("terminal", StringComparison.OrdinalIgnoreCase)),
            "The inbound MCP surface must not advertise filesystem mutation or process execution tools.");
    }

    [Test]
    public async Task ListModelsAsync_ReturnsDetailedOrderedModelsAndMarksTheExactDefault()
    {
        var harness = new Harness();
        harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            Descriptor("zeta-embed", 20),
            Descriptor("alpha-chat", 10),
            Descriptor("unavailable", 30, isAvailable: false)
        ]);
        harness.NodeSettingsAdministration.GetAgenticViewAsync(Arg.Any<CancellationToken>()).Returns(
            new NodeSettingsAgenticView("zeta-embed", null, null, null, null, null, null, null, null, 600, null, null, null, null, null, null, null, null));

        var models = await harness.Tools.ListModelsAsync(CancellationToken.None);

        AssertEx.Equal(2, models.Count);
        AssertEx.Equal("alpha-chat", models[0].Name);
        AssertEx.Equal(10L, models[0].SizeBytes!.Value);
        AssertEx.Equal("chat", models[0].Kind);
        AssertEx.False(models[0].IsDefault);
        AssertEx.Equal("zeta-embed", models[1].Name);
        AssertEx.Equal("embedding", models[1].Kind);
        AssertEx.True(models[1].IsDefault);
    }

    [Test]
    public void LifecycleToolSchemas_AdvertiseSnakeCaseRequiredAndOptionalArguments()
    {
        var harness = new Harness();

        AssertSchema(harness.Tools,
            nameof(NodeAgentMcpTools.StartAgentRunAsync),
            ["request_id", "task"],
            ["request_id", "task", "agent", "model", "model_override", "instructions", "workspace_id"]);
        AssertSchema(harness.Tools,
            nameof(NodeAgentMcpTools.GetAgentRunAsync),
            ["request_id"],
            ["request_id"]);
        AssertSchema(harness.Tools,
            nameof(NodeAgentMcpTools.CancelAgentRunAsync),
            ["request_id"],
            ["request_id"]);
        AssertSchema(harness.Tools,
            nameof(NodeAgentMcpTools.ListAgentRunsAsync),
            [],
            ["limit", "status"]);
        AssertSchema(harness.Tools,
            nameof(NodeAgentMcpTools.ListWorkspacesAsync),
            [],
            []);
    }

    [Test]
    public void LifecycleMethodSignatures_KeepNullDefaultsForEveryOptionalSchemaArgument()
    {
        AssertNullDefaults(nameof(NodeAgentMcpTools.StartAgentRunAsync),
            "agent",
            "model",
            "model_override",
            "instructions",
            "workspace_id");
        AssertNullDefaults(nameof(NodeAgentMcpTools.ListAgentRunsAsync), "limit", "status");
    }

    [Test]
    public async Task ListWorkspacesAsync_ReturnsOnlyOpaqueIdsAliasesAndReadOnlyMode()
    {
        var harness = new Harness();
        var workspaceId = Guid.NewGuid().ToString("D");
        harness.WorkspaceResolver.References = [new SelectedFolderReference(workspaceId, "engine")];

        var response = await harness.Tools.ListWorkspacesAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(response);

        AssertEx.Equal("ok", response.Status);
        AssertEx.Equal(1, response.Count);
        AssertEx.Equal(workspaceId, response.Workspaces[0].Id);
        AssertEx.Equal("engine", response.Workspaces[0].Alias);
        AssertEx.Equal("read-only", response.Workspaces[0].Mode);
        AssertEx.False(json.Contains("path", StringComparison.OrdinalIgnoreCase), "Workspace discovery must not expose a path field.");
    }

    [Test]
    public async Task ListWorkspacesAsync_WhenReferencesExceedBound_ReturnsTruncatedPage()
    {
        var harness = new Harness(maxListLimit: 2);
        harness.WorkspaceResolver.References =
        [
            new SelectedFolderReference(Guid.NewGuid().ToString("D"), "one"),
            new SelectedFolderReference(Guid.NewGuid().ToString("D"), "two"),
            new SelectedFolderReference(Guid.NewGuid().ToString("D"), "three")
        ];

        var response = await harness.Tools.ListWorkspacesAsync(CancellationToken.None);

        AssertEx.Equal(2, response.Workspaces.Count);
        AssertEx.Equal(3, response.Count);
        AssertEx.True(response.Truncated, "A bounded workspace list must report omitted references.");
    }

    [Test]
    [Arguments(McpAgentRunStartKind.Accepted, "accepted")]
    [Arguments(McpAgentRunStartKind.Existing, "existing")]
    [Arguments(McpAgentRunStartKind.ResultExpired, "result_expired")]
    [Arguments(McpAgentRunStartKind.RequestIdConflict, "conflict")]
    [Arguments(McpAgentRunStartKind.CapacityExceeded, "capacity")]
    [Arguments(McpAgentRunStartKind.Rejected, "rejected")]
    public async Task StartAgentRunAsync_WhenCoordinatorReturnsKind_MapsStableStatus(McpAgentRunStartKind kind, string expectedStatus)
    {
        var harness = new Harness();
        var view = CreateRunView(McpAgentRunStatus.Queued);
        harness.RunCoordinator.StartResult = new McpAgentRunStartResult(kind,
            view,
            kind is McpAgentRunStartKind.Accepted or McpAgentRunStartKind.Existing ? null : "bounded_failure",
            "Stable response.");

        var response = await harness.Tools.StartAgentRunAsync(view.RequestId.ToString("D"),
            "inspect",
            CancellationToken.None,
            model: Model);

        AssertEx.Equal(expectedStatus, response.Status);
        AssertEx.Equal("Stable response.", response.DisplayMessage);
        if (kind is McpAgentRunStartKind.Accepted or McpAgentRunStartKind.Existing)
        {
            AssertEx.Null(response.FailureCode);
        }
        else
        {
            AssertEx.Equal("bounded_failure", response.FailureCode);
        }
    }

    [Test]
    public async Task StartAgentRunAsync_WithValidRequest_ForwardsBindingWorkspaceAndCancellationToken()
    {
        var harness = new Harness();
        var requestId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        using var source = new CancellationTokenSource();
        harness.RunCoordinator.StartResult = new McpAgentRunStartResult(McpAgentRunStartKind.Accepted,
            CreateRunView(McpAgentRunStatus.Queued) with
            {
                RequestId = requestId,
                WorkspaceId = workspaceId
            },
            null,
            "Accepted.");

        await harness.Tools.StartAgentRunAsync(requestId.ToString("D"),
            "inspect",
            source.Token,
            agent: Agent,
            model_override: Model,
            instructions: "read only",
            workspace_id: workspaceId.ToString("D"));

        var request = AssertEx.NotNull(harness.RunCoordinator.LastStartRequest);
        AssertEx.Equal(requestId, request.RequestId);
        AssertEx.Equal("inspect", request.Task);
        AssertEx.Equal(Agent, request.Binding.AgentKey!);
        AssertEx.Equal(Model, request.Binding.ModelOverrideId!);
        AssertEx.Equal("read only", request.Binding.Instructions!);
        AssertEx.Equal(workspaceId, request.WorkspaceId!.Value);
        AssertEx.Equal(source.Token, harness.RunCoordinator.LastStartCancellationToken);
    }

    [Test]
    public async Task StartAgentRunAsync_WithPathLikeWorkspaceId_ReturnsPathFreeRejectionWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync(Guid.NewGuid().ToString("D"),
            "inspect",
            CancellationToken.None,
            agent: Agent,
            model_override: Model,
            workspace_id: "/private/repository");
        var json = JsonSerializer.Serialize(response);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal(McpAgentRunFailureCodes.WorkspaceNotAuthorized, response.FailureCode!);
        AssertEx.False(json.Contains("/private/repository", StringComparison.Ordinal), "Invalid path input must not be reflected.");
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task StartAgentRunAsync_WithInvalidRequestId_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync("not-a-uuid",
            "inspect",
            CancellationToken.None,
            model: Model);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task StartAgentRunAsync_WithBothBindings_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync(Guid.NewGuid().ToString("D"),
            "inspect",
            CancellationToken.None,
            agent: Agent,
            model: Model);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task StartAgentRunAsync_WithoutAnyBinding_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync(Guid.NewGuid().ToString("D"),
            "inspect",
            CancellationToken.None);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task StartAgentRunAsync_WithBlankTask_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync(Guid.NewGuid().ToString("D"),
            "   ",
            CancellationToken.None,
            model: Model);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task StartAgentRunAsync_WithNonCanonicalCompactUuid_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.StartAgentRunAsync(Guid.NewGuid().ToString("N"),
            "inspect",
            CancellationToken.None,
            model: Model);

        AssertEx.Equal("rejected", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.StartCallCount);
    }

    [Test]
    public async Task GetAgentRunAsync_WhenRunDoesNotExist_ReturnsStableNotFound()
    {
        var harness = new Harness();

        var response = await harness.Tools.GetAgentRunAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);

        AssertEx.Equal("not_found", response.Status);
        AssertEx.Equal("run_not_found", response.FailureCode!);
        AssertEx.Null(response.Run);
    }

    [Test]
    public async Task GetAgentRunAsync_WithInvalidRequestId_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.GetAgentRunAsync("/private/repository", CancellationToken.None);
        var json = JsonSerializer.Serialize(response);

        AssertEx.Equal("invalid_request", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.False(json.Contains("/private/repository", StringComparison.Ordinal), "Invalid request input must not be reflected.");
        AssertEx.Equal(0, harness.RunCoordinator.GetCallCount);
    }

    [Test]
    [Arguments(McpAgentRunStatus.Queued, "queued")]
    [Arguments(McpAgentRunStatus.Running, "running")]
    [Arguments(McpAgentRunStatus.Succeeded, "succeeded")]
    [Arguments(McpAgentRunStatus.Failed, "failed")]
    [Arguments(McpAgentRunStatus.Cancelled, "cancelled")]
    [Arguments(McpAgentRunStatus.Interrupted, "interrupted")]
    public async Task GetAgentRunAsync_WhenRunExists_ReturnsLifecycleStatus(McpAgentRunStatus status, string expectedStatus)
    {
        var harness = new Harness();
        harness.RunCoordinator.GetResult = CreateRunView(status);

        var response = await harness.Tools.GetAgentRunAsync(harness.RunCoordinator.GetResult.RequestId.ToString("D"), CancellationToken.None);

        AssertEx.Equal(expectedStatus, response.Status);
        AssertEx.Equal(expectedStatus, response.Run!.Metadata.Status);
    }

    [Test]
    public async Task GetAgentRunAsync_WhenResultEqualsLimit_ReturnsUnchangedResult()
    {
        var harness = new Harness(maxResultCharacters: 8);
        harness.RunCoordinator.GetResult = CreateRunView(McpAgentRunStatus.Succeeded) with
        {
            Result = "12345678"
        };

        var response = await harness.Tools.GetAgentRunAsync(harness.RunCoordinator.GetResult.RequestId.ToString("D"), CancellationToken.None);

        AssertEx.Equal("12345678", response.Run!.Result!);
        AssertEx.False(response.Run.ResultTruncated, "A result exactly at the configured bound must not be marked truncated.");
    }

    [Test]
    public async Task GetAgentRunAsync_WhenResultExceedsLimit_ReturnsBoundedResultAndTruncationFlag()
    {
        var harness = new Harness(maxResultCharacters: 8);
        harness.RunCoordinator.GetResult = CreateRunView(McpAgentRunStatus.Succeeded) with
        {
            Result = "123456789"
        };

        var response = await harness.Tools.GetAgentRunAsync(harness.RunCoordinator.GetResult.RequestId.ToString("D"), CancellationToken.None);

        AssertEx.Equal("12345678", response.Run!.Result!);
        AssertEx.True(response.Run.ResultTruncated, "A clipped result must explicitly report truncation.");
    }

    [Test]
    public async Task GetAgentRunAsync_WhenPayloadExpired_ReturnsTruthfulResultExpiredMetadataWithoutContent()
    {
        var harness = new Harness();
        harness.RunCoordinator.GetResult = CreateRunView(McpAgentRunStatus.Succeeded) with
        {
            Result = null,
            PayloadExpired = true,
            CompactedAtUtc = 99
        };

        var response = await harness.Tools.GetAgentRunAsync(harness.RunCoordinator.GetResult.RequestId.ToString("D"), CancellationToken.None);

        AssertEx.True(response.Run!.Metadata.ResultExpired, "Expired result payloads must be reported truthfully.");
        AssertEx.True(response.Run.Metadata.Compacted, "Compacted payloads must be distinguishable from active empty results.");
        AssertEx.Equal("result_expired", response.Status);
        AssertEx.Equal("result_expired", response.FailureCode!);
        AssertEx.Null(response.Run.Result);
    }

    [Test]
    [Arguments(McpAgentRunCancelKind.Requested, "requested")]
    [Arguments(McpAgentRunCancelKind.AlreadyRequested, "already")]
    [Arguments(McpAgentRunCancelKind.AlreadyTerminal, "terminal")]
    [Arguments(McpAgentRunCancelKind.NotFound, "not_found")]
    [Arguments(McpAgentRunCancelKind.Conflict, "conflict")]
    public async Task CancelAgentRunAsync_WhenCoordinatorReturnsKind_MapsStableStatus(McpAgentRunCancelKind kind, string expectedStatus)
    {
        var harness = new Harness();
        var requestId = Guid.NewGuid();
        harness.RunCoordinator.CancelResult = new McpAgentRunCancelResult(kind,
            kind == McpAgentRunCancelKind.NotFound
                ? null
                : CreateRunView(McpAgentRunStatus.Running) with
                {
                    RequestId = requestId
                },
            "Stable cancellation response.");

        var response = await harness.Tools.CancelAgentRunAsync(requestId.ToString("D"), CancellationToken.None);

        AssertEx.Equal(expectedStatus, response.Status);
        AssertEx.Equal("Stable cancellation response.", response.DisplayMessage);
    }

    [Test]
    public async Task CancelAgentRunAsync_WhenRunIsNotFound_ReturnsStableFailureCode()
    {
        var harness = new Harness();

        var response = await harness.Tools.CancelAgentRunAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);

        AssertEx.Equal("not_found", response.Status);
        AssertEx.Equal("run_not_found", response.FailureCode!);
    }

    [Test]
    public async Task CancelAgentRunAsync_WhenStateConflicts_ReturnsStableFailureCode()
    {
        var harness = new Harness();
        harness.RunCoordinator.CancelResult = new McpAgentRunCancelResult(McpAgentRunCancelKind.Conflict,
            CreateRunView(McpAgentRunStatus.Running),
            "State changed.");

        var response = await harness.Tools.CancelAgentRunAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);

        AssertEx.Equal("conflict", response.Status);
        AssertEx.Equal("state_conflict", response.FailureCode!);
    }

    [Test]
    public async Task CancelAgentRunAsync_WithInvalidRequestId_ReturnsPathFreeValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.CancelAgentRunAsync("/private/repository", CancellationToken.None);
        var json = JsonSerializer.Serialize(response);

        AssertEx.Equal("not_found", response.Status);
        AssertEx.Equal("invalid_request", response.FailureCode!);
        AssertEx.False(json.Contains("/private/repository", StringComparison.Ordinal), "Invalid request input must not be reflected.");
        AssertEx.Equal(0, harness.RunCoordinator.CancelCallCount);
    }

    [Test]
    public async Task ListAgentRunsAsync_WithoutLimit_UsesConfiguredDefaultAndOmitsResultContent()
    {
        var harness = new Harness(defaultListLimit: 7, maxListLimit: 10);
        harness.RunCoordinator.ListResults =
        [
            CreateRunView(McpAgentRunStatus.Succeeded) with
            {
                Result = "/private/result"
            }
        ];

        var response = await harness.Tools.ListAgentRunsAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(response);

        AssertEx.Equal(7, harness.RunCoordinator.LastListLimit);
        AssertEx.Equal(7, response.Limit);
        AssertEx.Equal(1, response.Count);
        AssertEx.False(json.Contains("\"result\":", StringComparison.OrdinalIgnoreCase),
            "List responses must not contain a model-result content field.");
        AssertEx.False(json.Contains("\"task\":", StringComparison.OrdinalIgnoreCase), "List responses must not contain task text.");
        AssertEx.False(json.Contains("\"instructions\":", StringComparison.OrdinalIgnoreCase), "List responses must not contain instructions.");
        AssertEx.False(json.Contains("/private/result", StringComparison.Ordinal), "List responses must never contain result content.");
    }

    [Test]
    public async Task ListAgentRunsAsync_WithLimitOutsideBounds_ClampsToConfiguredRange()
    {
        var harness = new Harness(defaultListLimit: 5, maxListLimit: 10);

        await harness.Tools.ListAgentRunsAsync(CancellationToken.None, limit: 0);
        AssertEx.Equal(1, harness.RunCoordinator.LastListLimit);

        await harness.Tools.ListAgentRunsAsync(CancellationToken.None, limit: 99);
        AssertEx.Equal(10, harness.RunCoordinator.LastListLimit);
    }

    [Test]
    public async Task ListAgentRunsAsync_WithCaseInsensitiveStatus_ForwardsParsedStatus()
    {
        var harness = new Harness();

        var response = await harness.Tools.ListAgentRunsAsync(CancellationToken.None, status: "SuCcEeDeD");

        AssertEx.Equal("ok", response.Status);
        AssertEx.Equal(McpAgentRunStatus.Succeeded, harness.RunCoordinator.LastListStatus!.Value);
    }

    [Test]
    public async Task ListAgentRunsAsync_WithUnknownStatus_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.ListAgentRunsAsync(CancellationToken.None, status: "finished");

        AssertEx.Equal("invalid_status", response.Status);
        AssertEx.Equal("invalid_status", response.FailureCode!);
        AssertEx.Equal(0, harness.RunCoordinator.ListCallCount);
    }

    [Test]
    public async Task ListAgentRunsAsync_WithNumericStatus_ReturnsStableValidationWithoutCoordinatorCall()
    {
        var harness = new Harness();

        var response = await harness.Tools.ListAgentRunsAsync(CancellationToken.None, status: "2");

        AssertEx.Equal("invalid_status", response.Status);
        AssertEx.Equal(0, harness.RunCoordinator.ListCallCount);
    }

    [Test]
    public async Task RunAgentAsync_WhenTaskIsBlank_ReturnsValidationWithoutSpawning()
    {
        var harness = new Harness();

        var result = await harness.Tools.RunAgentAsync("   ", harness.Progress, CancellationToken.None, model: Model);

        AssertEx.Equal("Cannot run: provide a non-empty task.", result);
        await harness.McpService.DidNotReceive().SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenBothBindingsAreProvided_ReturnsValidationWithoutSpawning()
    {
        var harness = new Harness();

        var result = await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None, agent: Agent, model: Model);

        AssertEx.Equal("Cannot run: provide exactly one of agent or model.", result);
        await harness.McpService.DidNotReceive().SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenNoBindingIsProvided_ReturnsValidationWithoutSpawning()
    {
        var harness = new Harness();

        var result = await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None);

        AssertEx.Equal("Cannot run: provide exactly one of agent or model.", result);
        await harness.McpService.DidNotReceive().SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenModelBindingIsProvided_ForwardsBareModelRequest()
    {
        var harness = new Harness("model response");

        var result = await harness.Tools.RunAgentAsync("inspect the parser",
            harness.Progress,
            CancellationToken.None,
            model: Model,
            instructions: "Return concise evidence.");

        AssertEx.Equal("model response", result);
        await harness.McpService.Received(1).SpawnForMcpAsync(Arg.Is<McpExecutionBindingRequest>(request =>
                request.ModelId == Model
                && request.AgentKey == null
                && request.ModelOverrideId == null
                && request.Instructions == "Return concise evidence."),
            "inspect the parser",
            expectedBindingFingerprint: null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenAgentBindingIsProvided_ForwardsSavedAgentRequest()
    {
        var harness = new Harness("agent response");

        var result = await harness.Tools.RunAgentAsync("find the call site", harness.Progress, CancellationToken.None, agent: Agent);

        AssertEx.Equal("agent response", result);
        await harness.McpService.Received(1).SpawnForMcpAsync(Arg.Is<McpExecutionBindingRequest>(request =>
                request.AgentKey == Agent
                && request.ModelId == null
                && request.ModelOverrideId == null),
            "find the call site",
            expectedBindingFingerprint: null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenModelOverrideIsProvided_ForwardsDistinctSavedAgentOverride()
    {
        var harness = new Harness("coder response");

        var result = await harness.Tools.RunAgentAsync("find the parser",
            harness.Progress,
            CancellationToken.None,
            agent: Agent,
            modelOverride: Model);

        AssertEx.Equal("coder response", result);
        await harness.McpService.Received(1).SpawnForMcpAsync(Arg.Is<McpExecutionBindingRequest>(request =>
                request.AgentKey == Agent
                && request.ModelId == null
                && request.ModelOverrideId == Model),
            "find the parser",
            expectedBindingFingerprint: null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAgentAsync_WhenOpaqueWorkspaceIdIsProvided_ForwardsParsedIdentifier()
    {
        var harness = new Harness("coder response");
        var workspaceId = Guid.NewGuid();

        var result = await harness.Tools.RunAgentAsync("find the parser",
            harness.Progress,
            CancellationToken.None,
            agent: Agent,
            modelOverride: Model,
            workspace_id: workspaceId.ToString("D"));

        AssertEx.Equal("coder response", result);
        await harness.McpService.Received(1).SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
            "find the parser",
            expectedBindingFingerprint: null,
            Arg.Any<CancellationToken>(),
            workspaceId);
    }

    [Test]
    public async Task RunAgentAsync_WhenCoderHasNoWorkspace_ReturnsStableAuthorizationRejection()
    {
        var harness = new Harness();
        harness.McpService.SpawnForMcpAsync(Arg.Is<McpExecutionBindingRequest>(request => request.AgentKey == Agent),
                   Arg.Any<string>(),
                   Arg.Any<string?>(),
                   Arg.Any<CancellationToken>(),
                   Arg.Is<Guid?>(workspaceId => workspaceId == null))
               .Returns(SpawnOutcome.Rejected(McpExecutionFailureCodes.WorkspaceNotAuthorized,
                   "Cannot run: the selected workspace is not authorized."));

        var result = await harness.Tools.RunAgentAsync("find the parser",
            harness.Progress,
            CancellationToken.None,
            agent: Agent,
            modelOverride: Model);

        AssertEx.Equal("Cannot run: the selected workspace is not authorized.", result);
        AssertEx.False(result.Contains("workspace_not_authorized", StringComparison.Ordinal),
            "synchronous tool result must keep stable codes internal.");
    }

    [Test]
    public async Task RunAgentAsync_WhenWorkspaceIdIsInvalid_ReturnsPathFreeAuthorizationFailureWithoutSpawning()
    {
        var harness = new Harness();

        var result = await harness.Tools.RunAgentAsync("find the parser",
            harness.Progress,
            CancellationToken.None,
            agent: Agent,
            modelOverride: Model,
            workspace_id: "/private/repo");

        AssertEx.Equal("Cannot run: the selected workspace is not authorized.", result);
        AssertEx.False(result.Contains("/private/repo", StringComparison.Ordinal), "invalid workspace input must not be reflected.");
        await harness.McpService.DidNotReceiveWithAnyArgs().SpawnForMcpAsync(default!, default!, default, default);
    }

    [Test]
    public void RunAgentSchema_AdvertisesOptionalOpaqueWorkspaceIdWithoutHostPathSurface()
    {
        var harness = new Harness();
        var method = typeof(NodeAgentMcpTools).GetMethod(nameof(NodeAgentMcpTools.RunAgentAsync));
        AssertEx.NotNull(method);
        var protocolTool = McpServerTool.Create(method!, harness.Tools, new McpServerToolCreateOptions()).ProtocolTool;
        var schemaJson = JsonSerializer.Serialize(protocolTool.InputSchema);
        using var schema = JsonDocument.Parse(schemaJson);
        var properties = schema.RootElement.GetProperty("properties");

        AssertEx.True(properties.TryGetProperty("workspace_id", out var workspace), "run_agent must advertise workspace_id.");
        var workspaceType = workspace.GetProperty("type");
        AssertEx.True(workspaceType.ValueKind == JsonValueKind.String
                ? string.Equals(workspaceType.GetString(), "string", StringComparison.Ordinal)
                : workspaceType.ValueKind == JsonValueKind.Array
                  && workspaceType.EnumerateArray()
                                  .Any(static item => string.Equals(item.GetString(), "string", StringComparison.Ordinal)),
            "workspace_id must be represented as a string, with nullable schema encoding allowed.");
        AssertEx.False(schema.RootElement.GetProperty("required").EnumerateArray()
                             .Any(static item => string.Equals(item.GetString(), "workspace_id", StringComparison.Ordinal)),
            "workspace_id must remain optional for bare/general tool-less runs.");
        AssertEx.False(schemaJson.Contains("hostPath", StringComparison.OrdinalIgnoreCase), "schema must not advertise host paths.");
        AssertEx.False(schemaJson.Contains("host_path", StringComparison.OrdinalIgnoreCase), "schema must not advertise host paths.");
    }

    [Test]
    public async Task RunAgentAsync_WhenExecutionIsRejected_ReturnsSanitizedDisplayMessage()
    {
        var harness = new Harness();
        harness.McpService.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                   Arg.Any<string>(),
                   Arg.Any<string?>(),
                   Arg.Any<CancellationToken>())
               .Returns(SpawnOutcome.Rejected("capacity_declined", "Cannot run: the local model is busy."));

        var result = await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None, model: Model);

        AssertEx.Equal("Cannot run: the local model is busy.", result);
        AssertEx.False(result.Contains("capacity_declined", StringComparison.Ordinal),
            "the synchronous MCP tool result must not expose the stable failure code.");
    }

    [Test]
    public async Task RunAgentAsync_WhenRunCompletes_ReportsAdmissionRunningAndCompletionProgress()
    {
        var harness = new Harness("done");

        await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None, model: Model);

        AssertEx.Equal(3, harness.Progress.Values.Count);
        AssertEx.Equal(0f, harness.Progress.Values[0].Progress);
        AssertEx.Equal("Admitting the run on the local node…", harness.Progress.Values[0].Message);
        AssertEx.Equal(0.1f, harness.Progress.Values[1].Progress);
        AssertEx.Equal("Running on the local model…", harness.Progress.Values[1].Message);
        AssertEx.Equal(1f, harness.Progress.Values[2].Progress);
        AssertEx.Equal("Completed.", harness.Progress.Values[2].Message);
    }

    [Test]
    public async Task RunAgentAsync_WhenCancellationTokenIsProvided_ForwardsTokenToSpawnService()
    {
        var harness = new Harness("done");
        using var source = new CancellationTokenSource();

        await harness.Tools.RunAgentAsync("inspect", harness.Progress, source.Token, model: Model);

        await harness.McpService.Received(1).SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
            "inspect",
            expectedBindingFingerprint: null,
            source.Token);
    }

    [Test]
    public async Task RunAgentAsync_WhenResultEqualsLimit_ReturnsResultWithoutMarker()
    {
        const int retainedCharacters = 24_000;
        var expected = new string('x', retainedCharacters);
        var harness = new Harness(expected);

        var result = await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None, model: Model);

        AssertEx.Equal(expected, result);
    }

    [Test]
    public async Task RunAgentAsync_WhenResultExceedsLimit_ReturnsBoundedMarkedOutput()
    {
        const int retainedCharacters = 24_000;
        const string marker = "\n\n[output truncated by the XE Local AI Engine MCP server]";
        var harness = new Harness(new string('x', retainedCharacters + 1));

        var result = await harness.Tools.RunAgentAsync("inspect", harness.Progress, CancellationToken.None, model: Model);

        AssertEx.Equal(retainedCharacters + marker.Length, result.Length);
        AssertEx.True(result.AsSpan(0, retainedCharacters).IndexOfAnyExcept('x') < 0,
            "the adapter must retain the first 24,000 result characters unchanged.");
        AssertEx.True(result.EndsWith(marker, StringComparison.Ordinal),
            "the bounded result must explain that truncation happened at the MCP server.");
    }

    private static void AssertSchema(NodeAgentMcpTools tools,
        string methodName,
        IReadOnlyList<string> requiredNames,
        IReadOnlyList<string> propertyNames)
    {
        var method = AssertEx.NotNull(typeof(NodeAgentMcpTools).GetMethod(methodName));
        var protocolTool = McpServerTool.Create(method, tools, new McpServerToolCreateOptions()).ProtocolTool;
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(protocolTool.InputSchema));
        var root = schema.RootElement;
        var actualProperties = root.GetProperty("properties")
                                   .EnumerateObject()
                                   .Select(static property => property.Name)
                                   .OrderBy(static name => name, StringComparer.Ordinal)
                                   .ToArray();
        var actualRequired = root.TryGetProperty("required", out var required)
            ? required.EnumerateArray()
                      .Select(static item => item.GetString()!)
                      .OrderBy(static name => name, StringComparer.Ordinal)
                      .ToArray()
            : [];

        AssertEx.Equal(string.Join('|', propertyNames.OrderBy(static name => name, StringComparer.Ordinal)),
            string.Join('|', actualProperties));
        AssertEx.Equal(string.Join('|', requiredNames.OrderBy(static name => name, StringComparer.Ordinal)),
            string.Join('|', actualRequired));
    }

    private static void AssertNullDefaults(string methodName, params string[] parameterNames)
    {
        var method = AssertEx.NotNull(typeof(NodeAgentMcpTools).GetMethod(methodName));
        var parameters = method.GetParameters().ToDictionary(static parameter => parameter.Name!, StringComparer.Ordinal);
        foreach (var parameterName in parameterNames)
        {
            AssertEx.True(parameters.TryGetValue(parameterName, out var parameter), $"Expected parameter '{parameterName}'.");
            AssertEx.True(parameter!.HasDefaultValue, $"'{parameterName}' must retain a default so the MCP schema keeps it optional.");
            AssertEx.Null(parameter.DefaultValue, $"'{parameterName}' must default to null.");
        }
    }

    private static McpAgentRunView CreateRunView(McpAgentRunStatus status) =>
        new(Guid.NewGuid(),
            status,
            Version: 3,
            McpAgentRunStopReason.None,
            Model,
            AgentDefinitionId: null,
            WorkspaceId: null,
            Result: null,
            DisplayMessage: "Stable response.",
            FailureCode: null,
            CreatedAtUtc: 10,
            ClaimedAtUtc: status == McpAgentRunStatus.Queued ? null : 20,
            CompletedAtUtc: status is McpAgentRunStatus.Queued or McpAgentRunStatus.Running ? null : 30,
            PayloadExpiresAtUtc: status is McpAgentRunStatus.Queued or McpAgentRunStatus.Running ? null : 86_400_030,
            CompactedAtUtc: null,
            PayloadExpired: false);

    private static LocalModelDescriptor Descriptor(string name, long sizeBytes, bool isAvailable = true) =>
        new()
        {
            ModelName = name,
            ProviderName = "llamacpp",
            IsAvailable = isAvailable,
            SizeBytes = sizeBytes,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null
        };

    private sealed class Harness
    {
        public Harness(string spawnResult = "unused",
            int maxResultCharacters = 24_000,
            int defaultListLimit = 20,
            int maxListLimit = 50)
        {
            McpService.SpawnForMcpAsync(Arg.Any<McpExecutionBindingRequest>(),
                          Arg.Any<string>(),
                          Arg.Any<string?>(),
                          Arg.Any<CancellationToken>(),
                          Arg.Any<Guid?>())
                      .Returns(SpawnOutcome.Success(spawnResult));
            Tools = new NodeAgentMcpTools(McpService,
                Substitute.For<IAgentDefinitionStore>(),
                GgufModelStore,
                NodeSettingsAdministration,
                Options.Create(new SpawnOptions
                {
                    MaxConcurrentSpawns = 2,
                    MaxCloudSpawns = 1
                }),
                RunCoordinator,
                WorkspaceResolver,
                Options.Create(new McpAgentRunOptions
                {
                    MaxResultCharacters = maxResultCharacters,
                    DefaultListLimit = defaultListLimit,
                    MaxListLimit = maxListLimit
                }),
                NullLogger<NodeAgentMcpTools>.Instance);
        }

        public IMcpAgentExecutionService McpService { get; } = Substitute.For<IMcpAgentExecutionService>();

        public IGgufModelStore GgufModelStore { get; } = Substitute.For<IGgufModelStore>();

        public INodeSettingsAdministrationService NodeSettingsAdministration { get; } = Substitute.For<INodeSettingsAdministrationService>();

        public RecordingProgress Progress { get; } = new();

        public FakeMcpAgentRunCoordinator RunCoordinator { get; } = new();

        public NodeAgentMcpTools Tools { get; }

        public FakeSelectedFolderResolver WorkspaceResolver { get; } = new();
    }

    private sealed class FakeMcpAgentRunCoordinator : IMcpAgentRunCoordinator
    {
        public int CancelCallCount { get; private set; }

        public McpAgentRunCancelResult CancelResult { get; set; } =
            new(McpAgentRunCancelKind.NotFound, null, "Run not found.");

        public int GetCallCount { get; private set; }

        public McpAgentRunView? GetResult { get; set; }

        public int LastListLimit { get; private set; }

        public McpAgentRunStatus? LastListStatus { get; private set; }

        public McpAgentRunStartRequest? LastStartRequest { get; private set; }

        public CancellationToken LastStartCancellationToken { get; private set; }

        public int ListCallCount { get; private set; }

        public IReadOnlyList<McpAgentRunView> ListResults { get; set; } = [];

        public int StartCallCount { get; private set; }

        public McpAgentRunStartResult StartResult { get; set; } =
            new(McpAgentRunStartKind.Accepted, CreateRunView(McpAgentRunStatus.Queued), null, "Accepted.");

        public Task<McpAgentRunCancelResult> CancelAsync(Guid requestId, CancellationToken cancellationToken)
        {
            CancelCallCount++;
            return Task.FromResult(CancelResult);
        }

        public Task<McpAgentRunView?> GetAsync(Guid requestId, CancellationToken cancellationToken)
        {
            GetCallCount++;
            return Task.FromResult(GetResult);
        }

        public Task<IReadOnlyList<McpAgentRunView>> ListAsync(int? limit,
            McpAgentRunStatus? status,
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            LastListLimit = limit ?? 0;
            LastListStatus = status;
            return Task.FromResult(ListResults);
        }

        public Task<McpAgentRunStartResult> StartAsync(McpAgentRunStartRequest request, CancellationToken cancellationToken)
        {
            StartCallCount++;
            LastStartRequest = request;
            LastStartCancellationToken = cancellationToken;
            return Task.FromResult(StartResult);
        }
    }

    private sealed class FakeSelectedFolderResolver : ISelectedFolderResolver
    {
        public IReadOnlyList<SelectedFolderReference> References { get; set; } = [];

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(References);

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The inbound MCP tests never register workspaces.");

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The inbound MCP adapter lists opaque references only.");
    }

    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Values { get; } = [];

        public void Report(ProgressNotificationValue value)
        {
            Values.Add(value);
        }
    }
}
