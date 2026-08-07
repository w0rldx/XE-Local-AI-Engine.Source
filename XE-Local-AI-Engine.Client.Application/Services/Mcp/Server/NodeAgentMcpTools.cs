namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The tool surface this node exposes to EXTERNAL MCP clients (Claude Code, Claude Desktop, an IDE). The point of
///     the surface is delegation: an outside agent hands a task to a locally-hosted model instead of doing the work
///     itself, so private or bulky work never leaves the machine.
///     <para>
///         <b>Workspace-read-only by construction.</b> Lifecycle tools persist only their bounded run records and
///         cancellation markers. The delegated model cannot modify operator files. An MCP caller is a strictly lesser
///         principal than the browser operator and, crucially, has NO human-in-the-loop route to answer a tool-approval
///         request — the same reason unattended execution strips approval-required tools from its offer. Adding a
///         source-writing tool here is an operator decision that needs an approval story first, not an implementation
///         detail.
///     </para>
/// </summary>
[McpServerToolType]
public sealed class NodeAgentMcpTools
{
    /// <summary>
    ///     Upper bound on the characters returned from a single agent run. Claude Code caps MCP tool output at ~25k
    ///     tokens by default and warns past ~10k, so an unbounded local-model answer would be truncated by the client
    ///     with no indication of why. Bounding here means the caller gets a clean, explicit marker instead.
    /// </summary>
    private const string TruncationMarker = "\n\n[output truncated by the XE Local AI Engine MCP server]";

    private const string InvalidRequestCode = "invalid_request";
    private const string InvalidStatusCode = "invalid_status";
    private const string ResultExpiredCode = "result_expired";
    private const string RunNotFoundCode = "run_not_found";

    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILogger<NodeAgentMcpTools> _logger;
    private readonly IMcpAgentRunCoordinator _runCoordinator;
    private readonly McpAgentRunOptions _runOptions;
    private readonly ISelectedFolderResolver _selectedFolderResolver;
    private readonly SpawnOptions _spawnOptions;
    private readonly IMcpAgentExecutionService _mcpAgentExecutionService;

    public NodeAgentMcpTools(IMcpAgentExecutionService mcpAgentExecutionService,
        IAgentDefinitionStore agentDefinitionStore,
        IGgufModelStore ggufModelStore,
        IOptions<SpawnOptions> spawnOptions,
        IMcpAgentRunCoordinator runCoordinator,
        ISelectedFolderResolver selectedFolderResolver,
        IOptions<McpAgentRunOptions> runOptions,
        ILogger<NodeAgentMcpTools> logger)
    {
        _mcpAgentExecutionService = mcpAgentExecutionService ?? throw new ArgumentNullException(nameof(mcpAgentExecutionService));
        _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _runCoordinator = runCoordinator ?? throw new ArgumentNullException(nameof(runCoordinator));
        _selectedFolderResolver = selectedFolderResolver ?? throw new ArgumentNullException(nameof(selectedFolderResolver));
        ArgumentNullException.ThrowIfNull(runOptions);
        _runOptions = runOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [McpServerTool(Name = "list_agents")]
    [Description("List the saved agents (personas) on this node that can be given a task with run_agent or start_agent_run. Returns each agent's id, name and description.")]
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var definitions = await _agentDefinitionStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. definitions.Select(static definition => new AgentSummary(definition.Id.ToString(), definition.Name, definition.Description))];
    }

    [McpServerTool(Name = "list_models")]
    [Description("List the locally installed models on this node that run_agent or start_agent_run can bind directly when no saved agent is wanted.")]
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. models.Where(static model => model.IsAvailable)
                     .Select(static model => model.ModelName)
                     .OrderBy(static name => name, StringComparer.Ordinal)
        ];
    }

    [McpServerTool(Name = "list_workspaces")]
    [Description(
        "List the operator-authorized read-only workspaces that may be used by the seeded Coder. Returns only bounded opaque ids, aliases, and the read-only mode; host paths are never exposed. A workspace id remains valid across MCP connections until the operator revokes it.")]
    public async Task<McpWorkspaceListResponse> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var references = await _selectedFolderResolver.ListReferencesAsync(cancellationToken).ConfigureAwait(false);
        var bounded = references.Take(_runOptions.MaxListLimit)
                                .Select(static reference => new McpWorkspaceSummary(reference.Id, reference.Alias, "read-only"))
                                .ToArray();
        return new McpWorkspaceListResponse("ok", bounded, references.Count, references.Count > bounded.Length);
    }

    [McpServerTool(Name = "start_agent_run")]
    [Description(
        "Accept a durable background agent run and return immediately. Supply a globally unique UUID request_id plus exactly one of agent or model. The run continues across MCP disconnects, can be polled from a later connection, and never grants write access: bare/general runs are tool-less and the seeded Coder is limited to operator-authorized read-only workspace tools.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentRunStartResponse> StartAgentRunAsync(
        [Description(
            "A globally unique UUID in canonical lowercase-or-uppercase hyphenated form. Reusing it with the same request returns the existing run; reusing it for a different request is rejected.")]
        string request_id,
        [Description("The bounded task for the background local agent to carry out.")]
        string task,
        CancellationToken cancellationToken,
        [Description("A saved agent's id or name. Mutually exclusive with model.")]
        string? agent = null,
        [Description("A locally installed model id. Mutually exclusive with agent.")]
        string? model = null,
        [Description("A local model id for an unbound saved agent such as the seeded read-only Coder.")]
        string? model_override = null,
        [Description("Optional system-prompt override for a bare model. It is never returned by lifecycle tools.")]
        string? instructions = null,
        [Description("Optional opaque id from list_workspaces. Required for the seeded read-only Coder and never a host path.")]
        string? workspace_id = null)
#pragma warning restore CA1707, IDE1006
    {
        if (!TryParseRequestId(request_id, out var requestId) || string.IsNullOrWhiteSpace(task))
        {
            return RejectedStart(InvalidRequestCode, "Cannot start: provide a valid request UUID and non-empty bounded task.");
        }

        if (string.IsNullOrWhiteSpace(agent) == string.IsNullOrWhiteSpace(model))
        {
            return RejectedStart(InvalidRequestCode, "Cannot start: provide exactly one of agent or model.");
        }

        if (!TryParseOptionalWorkspaceId(workspace_id, out var workspaceId))
        {
            return RejectedStart(McpAgentRunFailureCodes.WorkspaceNotAuthorized,
                "Cannot start: the selected workspace is not authorized.");
        }

        var result = await _runCoordinator.StartAsync(new McpAgentRunStartRequest(requestId,
                task,
                new McpExecutionBindingRequest
                {
                    AgentKey = NullIfWhiteSpace(agent),
                    ModelId = NullIfWhiteSpace(model),
                    ModelOverrideId = NullIfWhiteSpace(model_override),
                    Instructions = instructions
                },
                workspaceId),
            cancellationToken).ConfigureAwait(false);

        return new McpAgentRunStartResponse(MapStartStatus(result.Kind),
            result.Run is null ? null : McpAgentToolResponseMapper.ToSummary(result.Run),
            result.FailureCode,
            result.DisplayMessage);
    }

    [McpServerTool(Name = "get_agent_run")]
    [Description(
        "Poll a durable background run by its globally unique request UUID, including from a later MCP connection. Returns bounded lifecycle metadata and at most 24,000 result characters with an explicit result_truncated flag. Expired or compacted payloads are reported truthfully; task, instructions, and host paths are never returned.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentRunGetResponse> GetAgentRunAsync([Description("The canonical hyphenated UUID supplied to start_agent_run.")] string request_id,
        CancellationToken cancellationToken)
#pragma warning restore CA1707, IDE1006
    {
        if (!TryParseRequestId(request_id, out var requestId))
        {
            return new McpAgentRunGetResponse("invalid_request", null, InvalidRequestCode, "Cannot get: provide a valid request UUID.");
        }

        var run = await _runCoordinator.GetAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return new McpAgentRunGetResponse("not_found", null, RunNotFoundCode, "Run not found.");
        }

        var result = run.PayloadExpired ? null : run.Result;
        var resultTruncated = result?.Length > _runOptions.MaxResultCharacters;
        if (resultTruncated)
        {
            result = result![.._runOptions.MaxResultCharacters];
        }

        var responseStatus = run.PayloadExpired
            ? ResultExpiredCode
            : McpAgentToolResponseMapper.ToExternalValue(run.Status);
        var failureCode = run.PayloadExpired ? ResultExpiredCode : run.FailureCode;
        var displayMessage = run.PayloadExpired
            ? "The retained result for this request has expired."
            : run.DisplayMessage ?? "Run found.";
        return new McpAgentRunGetResponse(responseStatus,
            new McpAgentRunDetail(McpAgentToolResponseMapper.ToSummary(run), result, resultTruncated),
            failureCode,
            displayMessage);
    }

    [McpServerTool(Name = "cancel_agent_run")]
    [Description(
        "Durably request cancellation of a background run by UUID. The cancellation marker survives MCP disconnects and process restart. Expected races such as an already-terminal or already-requested run are returned as structured results, and no write-capable workspace access is introduced.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentRunCancelResponse> CancelAgentRunAsync([Description("The canonical hyphenated UUID supplied to start_agent_run.")] string request_id,
        CancellationToken cancellationToken)
#pragma warning restore CA1707, IDE1006
    {
        if (!TryParseRequestId(request_id, out var requestId))
        {
            return new McpAgentRunCancelResponse("not_found", null, InvalidRequestCode, "Cannot cancel: provide a valid request UUID.");
        }

        var result = await _runCoordinator.CancelAsync(requestId, cancellationToken).ConfigureAwait(false);
        return new McpAgentRunCancelResponse(MapCancelStatus(result.Kind),
            result.Run is null ? null : McpAgentToolResponseMapper.ToSummary(result.Run),
            MapCancelFailureCode(result.Kind),
            result.DisplayMessage);
    }

    [McpServerTool(Name = "list_agent_runs")]
    [Description(
        "List bounded content-free lifecycle metadata for durable background runs, including runs started by earlier MCP connections. An optional case-insensitive status filter may be supplied. Results never contain task text, instructions, model output, or host paths, and all workspace execution remains read-only.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentRunListResponse> ListAgentRunsAsync(CancellationToken cancellationToken,
        [Description("Maximum runs to return. Values are clamped to the server's configured bounded range.")]
        int? limit = null,
        [Description("Optional lifecycle status: queued, running, succeeded, failed, cancelled, or interrupted.")]
        string? status = null)
#pragma warning restore CA1707, IDE1006
    {
        McpAgentRunStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var canonicalStatus = Enum.GetNames<McpAgentRunStatus>()
                                      .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));
            if (canonicalStatus is null || !Enum.TryParse(canonicalStatus, out McpAgentRunStatus value))
            {
                return new McpAgentRunListResponse("invalid_status",
                    [],
                    0,
                    ClampListLimit(limit),
                    InvalidStatusCode,
                    "Cannot list: status must be queued, running, succeeded, failed, cancelled, or interrupted.");
            }

            parsedStatus = value;
        }

        var boundedLimit = ClampListLimit(limit);
        var runs = await _runCoordinator.ListAsync(boundedLimit, parsedStatus, cancellationToken).ConfigureAwait(false);
        return new McpAgentRunListResponse("ok",
            runs.Select(McpAgentToolResponseMapper.ToSummary).ToArray(),
            runs.Count,
            boundedLimit);
    }

    [McpServerTool(Name = "run_agent")]
    [Description(
        "Run a task on this node's local model and return the result. Supply either agent (a saved agent's id or name) or model (a local model id) — exactly one. Saved general agents and bare models are tool-less. The seeded read-only Coder may use only its three workspace-read tools, requires modelOverride because it intentionally has no pinned model, and requires an opaque workspace_id from list_workspaces. Runs are admission-gated: a request that would exceed the node's memory or concurrency limits is declined with a reason rather than queued indefinitely.")]
    // Parameter order is dictated by C#, not by preference: `progress` and `cancellationToken` are injected by the SDK
    // and excluded from the generated schema, but they carry no default, so they must precede the optional arguments.
    // Those defaults are load-bearing — the SDK derives `required` from the ABSENCE of a default, not from nullability,
    // so a nullable-but-defaultless `string? agent` is advertised as REQUIRED and every call that binds a bare model is
    // rejected by the binder before the handler runs (measured live: "the arguments dictionary is missing a value for
    // the required parameter 'agent'"). Do not remove the `= null`s.
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<string> RunAgentAsync([Description("The task for the local agent to carry out.")] string task,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        [Description("A saved agent's id or name. Mutually exclusive with model.")]
        string? agent = null,
        [Description("A local model id to bind an ad-hoc agent to. Mutually exclusive with agent.")]
        string? model = null,
        [Description("A local model id for an unbound saved agent such as Coder (read-only). Rejected for an agent that already pins a model.")]
        string? modelOverride = null,
        [Description("Optional system-prompt override. Only applies when binding a bare model; ignored when a saved agent is named.")]
        string? instructions = null,
        [Description("Optional opaque workspace id from list_workspaces. Required by the seeded read-only Coder and never a host path.")]
        string? workspace_id = null)
#pragma warning restore CA1707, IDE1006
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "Cannot run: provide a non-empty task.";
        }

        if (string.IsNullOrWhiteSpace(agent) == string.IsNullOrWhiteSpace(model))
        {
            return "Cannot run: provide exactly one of agent or model.";
        }

        Guid? workspaceId = null;
        if (!string.IsNullOrWhiteSpace(workspace_id))
        {
            if (!Guid.TryParse(workspace_id, out var parsedWorkspaceId) || parsedWorkspaceId == Guid.Empty)
            {
                return "Cannot run: the selected workspace is not authorized.";
            }

            workspaceId = parsedWorkspaceId;
        }

        // A local model can take well over a minute to load and generate. An MCP client aborts a call that produces
        // neither a response nor a progress notification inside its idle window (five minutes for Claude Code), so an
        // early progress report is what keeps a legitimate cold-start run alive rather than being killed as hung.
        progress.Report(new ProgressNotificationValue
        {
            Progress = 0f,
            Message = "Admitting the run on the local node…"
        });

        // The fan-out and cloud-spawn caps hang off a per-root-invocation SpawnContext, which a chat turn seeds and an
        // MCP call has no equivalent of. Seed one synthetic root per call so an MCP-driven run is bounded by exactly
        // the same caps as an operator-driven one instead of running uncapped.
        using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);

        var request = new McpExecutionBindingRequest
        {
            AgentKey = string.IsNullOrWhiteSpace(agent) ? null : agent,
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model,
            ModelOverrideId = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride,
            Instructions = instructions
        };

        progress.Report(new ProgressNotificationValue
        {
            Progress = 0.1f,
            Message = "Running on the local model…"
        });

        // The inbound execution service returns a typed, sanitized outcome for every EXPECTED rejection (over-cap, no
        // fit, busy, unresolved agent/model), so those reach the caller as an ordinary tool result. Only a genuinely
        // exceptional fault propagates, and the SDK turns that into a protocol error.
        var outcome = await _mcpAgentExecutionService.SpawnForMcpAsync(request,
            task,
            expectedBindingFingerprint: null,
            cancellationToken,
            workspaceId).ConfigureAwait(false);
        var result = outcome.ToSynchronousResult();

        progress.Report(new ProgressNotificationValue
        {
            Progress = 1f,
            Message = "Completed."
        });

        if (result.Length <= _runOptions.MaxResultCharacters)
        {
            return result;
        }

        _logger.LogInformation("An MCP run_agent result was truncated from {ActualLength} to {MaxLength} characters before returning it to the client.",
            result.Length,
            _runOptions.MaxResultCharacters);

        return string.Concat(result.AsSpan(0, _runOptions.MaxResultCharacters), TruncationMarker);
    }

    private static bool TryParseRequestId(string value, out Guid requestId) =>
        Guid.TryParseExact(value, "D", out requestId) && requestId != Guid.Empty;

    private static bool TryParseOptionalWorkspaceId(string? value, out Guid? workspaceId)
    {
        workspaceId = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        workspaceId = parsed;
        return true;
    }

    private static McpAgentRunStartResponse RejectedStart(string failureCode, string displayMessage) =>
        new("rejected", null, failureCode, displayMessage);

    private static string MapStartStatus(McpAgentRunStartKind kind) =>
        kind switch
        {
            McpAgentRunStartKind.Accepted => "accepted",
            McpAgentRunStartKind.Existing => "existing",
            McpAgentRunStartKind.ResultExpired => ResultExpiredCode,
            McpAgentRunStartKind.RequestIdConflict => "conflict",
            McpAgentRunStartKind.CapacityExceeded => "capacity",
            _ => "rejected"
        };

    private static string MapCancelStatus(McpAgentRunCancelKind kind) =>
        kind switch
        {
            McpAgentRunCancelKind.Requested => "requested",
            McpAgentRunCancelKind.AlreadyRequested => "already",
            McpAgentRunCancelKind.AlreadyTerminal => "terminal",
            McpAgentRunCancelKind.NotFound => "not_found",
            _ => "conflict"
        };

    private static string? MapCancelFailureCode(McpAgentRunCancelKind kind) =>
        kind switch
        {
            McpAgentRunCancelKind.NotFound => RunNotFoundCode,
            McpAgentRunCancelKind.Conflict => "state_conflict",
            _ => null
        };

    private int ClampListLimit(int? limit) =>
        Math.Clamp(limit ?? _runOptions.DefaultListLimit, 1, _runOptions.MaxListLimit);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>One saved agent, as offered to an external MCP client. Ids are stringified for a JSON-schema-friendly shape.</summary>
    public sealed record AgentSummary(string Id, string Name, string? Description);
}
