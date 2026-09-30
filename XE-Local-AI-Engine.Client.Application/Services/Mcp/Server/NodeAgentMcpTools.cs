namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The tool surface this node exposes to EXTERNAL MCP clients, for delegation: an outside agent hands a task to a
///     locally-hosted model instead of doing the work itself, so private or bulky work never leaves the machine.
/// </summary>
/// <remarks>
///     Delegate execution stays workspace-read-only by construction. An explicitly minted agentic key may run a saved
///     agent's complete allowed-tool set under strict audit-before-invocation auto-approval, but it still receives no
///     browser Operator role and no JWT authority.
/// </remarks>
[McpServerToolType]
[Authorize(Policy = NodeAuthorizationPolicies.McpServer)]
public sealed class NodeAgentMcpTools
{
    /// <summary>
    ///     Upper bound on the characters returned from a single agent run.
    /// </summary>
    /// <remarks>
    ///     An MCP client caps tool output — Claude Code at ~25k tokens by default, warning past ~10k — so an unbounded
    ///     local-model answer would be truncated by the client with no indication of why. Bounding here gives the
    ///     caller a clean, explicit marker instead.
    /// </remarks>
    private const string TruncationMarker = "\n\n[output truncated by the XE Local AI Engine MCP server]";

    /// <summary>
    ///     How often a synchronous run reports that it is still generating. An MCP client aborts a call that produces neither a
    ///     response nor a progress notification inside its idle window, and a long local generation used to report nothing (I-D15).
    /// </summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private const string InvalidRequestCode = "invalid_request";
    private const string InvalidStatusCode = "invalid_status";
    private const string ResultExpiredCode = "result_expired";
    private const string RunNotFoundCode = "run_not_found";

    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly INodeSettingsAdministrationService _nodeSettingsAdministrationService;
    private readonly ILogger<NodeAgentMcpTools> _logger;
    private readonly IMcpAgentRunCoordinator _runCoordinator;
    private readonly McpAgentRunOptions _runOptions;
    private readonly ISelectedFolderResolver _selectedFolderResolver;
    private readonly SpawnOptions _spawnOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpAgentExecutionService _mcpAgentExecutionService;

    public NodeAgentMcpTools(IMcpAgentExecutionService mcpAgentExecutionService,
        IAgentDefinitionStore agentDefinitionStore,
        IGgufModelStore ggufModelStore,
        INodeSettingsAdministrationService nodeSettingsAdministrationService,
        IOptions<SpawnOptions> spawnOptions,
        IMcpAgentRunCoordinator runCoordinator,
        ISelectedFolderResolver selectedFolderResolver,
        IOptions<McpAgentRunOptions> runOptions,
        TimeProvider timeProvider,
        ILogger<NodeAgentMcpTools> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _mcpAgentExecutionService = mcpAgentExecutionService ?? throw new ArgumentNullException(nameof(mcpAgentExecutionService));
        _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _nodeSettingsAdministrationService = nodeSettingsAdministrationService ?? throw new ArgumentNullException(nameof(nodeSettingsAdministrationService));
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _runCoordinator = runCoordinator ?? throw new ArgumentNullException(nameof(runCoordinator));
        _selectedFolderResolver = selectedFolderResolver ?? throw new ArgumentNullException(nameof(selectedFolderResolver));
        ArgumentNullException.ThrowIfNull(runOptions);
        _runOptions = runOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [McpServerTool(Name = "list_agents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the saved agents (personas) on this node that can be given a task with run_agent or start_agent_run. Returns each agent's id, name and description.")]
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var definitions = await _agentDefinitionStore.ListAsync(cancellationToken);
        return
        [
            .. definitions.Select(static definition => new AgentSummary
            {
                Id = definition.Id.ToString(),
                Name = definition.Name,
                Description = definition.Description
            })
        ];
    }

    [McpServerTool(Name = "list_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the locally installed models on this node that run_agent or start_agent_run can bind directly when no saved agent is wanted.")]
    public async Task<IReadOnlyList<LocalModelSummary>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);
        var settings = await _nodeSettingsAdministrationService.GetAgenticViewAsync(cancellationToken);
        return
        [
            .. models.Where(static model => model.IsAvailable)
                     .OrderBy(static model => model.ModelName, StringComparer.Ordinal)
                     .Select(model => new LocalModelSummary
                     {
                         Name = model.ModelName,
                         SizeBytes = model.SizeBytes,
                         Kind = ToWireKind(LocalGgufModelKindClassifier.Classify(model.ModelName)),
                         IsDefault = string.Equals(model.ModelName, settings.DefaultModelName, StringComparison.Ordinal)
                     })
        ];
    }

    [McpServerTool(Name = "list_workspaces", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List the operator-authorized read-only workspaces that may be used by the seeded Coder. Returns only bounded opaque ids, aliases, and the read-only mode; host paths are never exposed. A workspace id remains valid across MCP connections until the operator revokes it.")]
    public async Task<McpWorkspaceListResponse> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var references = await _selectedFolderResolver.ListReferencesAsync(cancellationToken);
        var bounded = references.Take(_runOptions.MaxListLimit)
                                .Select(static reference => new McpWorkspaceSummary
                                {
                                    Id = reference.Id,
                                    Alias = reference.Alias,
                                    Mode = "read-only"
                                })
                                .ToArray();
        return new McpWorkspaceListResponse
        {
            Status = "ok",
            Workspaces = bounded,
            Count = references.Count,
            Truncated = references.Count > bounded.Length
        };
    }

    [McpServerTool(Name = "start_agent_run", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Accept a durable background agent run and return immediately. Supply a globally unique UUID request_id plus exactly one of agent or model. The run continues across MCP disconnects and can be polled from a later connection. Delegate callers remain tool-less except for the seeded read-only Coder; agentic callers may use the saved agent's full allowed-tool set with strict audited auto-approval.")]
    // Tool parameter names are snake_case: they are MCP's public JSON contract.
    public async Task<McpAgentRunStartResponse> StartAgentRunAsync(
        [Description(
            "A globally unique UUID in canonical lowercase-or-uppercase hyphenated form. Reusing it with the same request returns the existing run; reusing it for a different request is rejected.")]
        string request_id,
        [Description("The bounded task for the background local agent to carry out.")]
        string task,
        CancellationToken cancellationToken,
        ClaimsPrincipal? user = null,
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
    {
        var inboundContext = McpInboundExecutionContext.FromPrincipal(user);
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

        var result = await _runCoordinator.StartAsync(new McpAgentRunStartRequest
            {
                RequestId = requestId,
                Task = task,
                Binding = new McpExecutionBindingRequest
                {
                    AgentKey = NullIfWhiteSpace(agent),
                    ModelId = NullIfWhiteSpace(model),
                    ModelOverrideId = NullIfWhiteSpace(model_override),
                    Instructions = instructions,
                    InboundContext = inboundContext,
                    ExecutionRequestId = requestId
                },
                WorkspaceId = workspaceId
            },
            cancellationToken);

        return new McpAgentRunStartResponse
        {
            Status = MapStartStatus(result.Kind),
            Run = result.Run is null ? null : McpAgentToolResponseMapper.ToSummary(result.Run),
            // An existing run whose result expired is a successful answer about that run, as in get_agent_run: the status and
            // run.metadata.failure_code carry result_expired, the top level stays clear.
            FailureCode = result.Kind == McpAgentRunStartKind.ResultExpired ? null : result.FailureCode,
            DisplayMessage = result.DisplayMessage
        };
    }

    [McpServerTool(Name = "get_agent_run", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Poll a durable background run by its globally unique request UUID, including from a later MCP connection. Returns bounded lifecycle metadata and at most 24,000 result characters with an explicit result_truncated flag. Expired or compacted payloads are reported truthfully; task, instructions, and host paths are never returned.")]
    public async Task<McpAgentRunGetResponse> GetAgentRunAsync([Description("The canonical hyphenated UUID supplied to start_agent_run.")] string request_id,
        CancellationToken cancellationToken)
    {
        if (!TryParseRequestId(request_id, out var requestId))
        {
            return new McpAgentRunGetResponse
            {
                Status = "invalid_request",
                Run = null,
                FailureCode = InvalidRequestCode,
                DisplayMessage = "Cannot get: provide a valid request UUID."
            };
        }

        var run = await _runCoordinator.GetAsync(requestId, cancellationToken);
        if (run is null)
        {
            return new McpAgentRunGetResponse
            {
                Status = "not_found",
                Run = null,
                FailureCode = RunNotFoundCode,
                DisplayMessage = "Run not found."
            };
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
        // The poll itself succeeded: a run that FAILED, or whose result expired, reports its code inside run.metadata, never at the
        // top level, or the filter would mark a correct status answer isError and a client would retry the poll (review 2026-09-30).
        var displayMessage = run.PayloadExpired
            ? "The retained result for this request has expired."
            : run.DisplayMessage ?? "Run found.";
        return new McpAgentRunGetResponse
        {
            Status = responseStatus,
            Run = new McpAgentRunDetail
            {
                Metadata = McpAgentToolResponseMapper.ToSummary(run),
                Result = result,
                ResultTruncated = resultTruncated
            },
            FailureCode = null,
            DisplayMessage = displayMessage
        };
    }

    [McpServerTool(Name = "cancel_agent_run", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Durably request cancellation of a background run by UUID. The cancellation marker survives MCP disconnects and process restart. Expected races such as an already-terminal or already-requested run are returned as structured results, and no write-capable workspace access is introduced.")]
    public async Task<McpAgentRunCancelResponse> CancelAgentRunAsync([Description("The canonical hyphenated UUID supplied to start_agent_run.")] string request_id,
        CancellationToken cancellationToken)
    {
        if (!TryParseRequestId(request_id, out var requestId))
        {
            // Status and failure_code agree, as in get_agent_run: a malformed id is an invalid request, not a missing run (I-D9).
            return new McpAgentRunCancelResponse
            {
                Status = InvalidRequestCode,
                Run = null,
                FailureCode = InvalidRequestCode,
                DisplayMessage = "Cannot cancel: provide a valid request UUID."
            };
        }

        var result = await _runCoordinator.CancelAsync(requestId, cancellationToken);
        return new McpAgentRunCancelResponse
        {
            Status = MapCancelStatus(result.Kind),
            Run = result.Run is null ? null : McpAgentToolResponseMapper.ToSummary(result.Run),
            FailureCode = MapCancelFailureCode(result.Kind),
            DisplayMessage = result.DisplayMessage
        };
    }

    [McpServerTool(Name = "list_agent_runs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List bounded content-free lifecycle metadata for durable background runs, including runs started by earlier MCP connections. An optional case-insensitive status filter may be supplied. Results never contain task text, instructions, model output, or host paths, and all workspace execution remains read-only.")]
    public async Task<McpAgentRunListResponse> ListAgentRunsAsync(CancellationToken cancellationToken,
        [Description("Maximum runs to return. Values are clamped to the server's configured bounded range.")]
        int? limit = null,
        [Description("Optional lifecycle status: queued, running, succeeded, failed, cancelled, or interrupted.")]
        string? status = null)
    {
        McpAgentRunStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var canonicalStatus = Enum.GetNames<McpAgentRunStatus>()
                                      .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));
            if (canonicalStatus is null || !Enum.TryParse(canonicalStatus, out McpAgentRunStatus value))
            {
                return new McpAgentRunListResponse
                {
                    Status = "invalid_status",
                    Runs = [],
                    Count = 0,
                    Limit = ClampListLimit(limit),
                    FailureCode = InvalidStatusCode,
                    DisplayMessage = "Cannot list: status must be queued, running, succeeded, failed, cancelled, or interrupted."
                };
            }

            parsedStatus = value;
        }

        var boundedLimit = ClampListLimit(limit);
        var runs = await _runCoordinator.ListAsync(boundedLimit, parsedStatus, cancellationToken);
        return new McpAgentRunListResponse
        {
            Status = "ok",
            Runs = runs.Select(McpAgentToolResponseMapper.ToSummary).ToArray(),
            Count = runs.Count,
            Limit = boundedLimit
        };
    }

    [McpServerTool(Name = "run_agent", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Run a task on this node's local model and return the result. Supply either agent (a saved agent's id or name) or model (a local model id) — exactly one. Delegate saved agents and bare models are tool-less; the seeded read-only Coder may use only its three workspace-read tools. Agentic saved-agent runs may use the definition's full allowed-tool set with strict audited auto-approval. Runs are admission-gated: a request that would exceed the node's memory or concurrency limits is declined with a reason rather than queued indefinitely.")]
    // Parameter order is dictated by C#: the SDK-injected `progress` and `cancellationToken` carry no default, so they precede the
    // optional arguments, whose defaults are load-bearing — the SDK derives `required` from the ABSENCE of a default, not from nullability, so a defaultless parameter is advertised REQUIRED.
    public async Task<CallToolResult> RunAgentAsync([Description("The task for the local agent to carry out. Bounded to 32 KiB of UTF-8.")] string task,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        ClaimsPrincipal? user = null,
        [Description("A saved agent's id or name. Mutually exclusive with model.")]
        string? agent = null,
        [Description("A local model id to bind an ad-hoc agent to. Mutually exclusive with agent.")]
        string? model = null,
        [Description("A local model id for an unbound saved agent such as Coder (read-only). Rejected for an agent that already pins a model. Spelled modelOverride here; start_agent_run spells it model_override.")]
        string? modelOverride = null,
        [Description("Optional system-prompt override. Only applies when binding a bare model; ignored when a saved agent is named.")]
        string? instructions = null,
        [Description("Optional opaque workspace id from list_workspaces. Required by the seeded read-only Coder and never a host path.")]
        string? workspace_id = null)
    {
        var inboundContext = McpInboundExecutionContext.FromPrincipal(user);
        if (string.IsNullOrWhiteSpace(task))
        {
            return McpToolResults.Failure(InvalidRequestCode, "Cannot run: provide a non-empty task.");
        }

        if (Encoding.UTF8.GetByteCount(task) > _runOptions.MaxTaskUtf8Bytes)
        {
            return McpToolResults.Failure(McpExecutionFailureCodes.TaskTooLarge, McpAgentRunText.TaskTooLargeMessage(_runOptions.MaxTaskUtf8Bytes));
        }

        // The same instructions bound start_agent_run applies, so the two run tools reject the same inputs (live re-run J7b).
        if (Encoding.UTF8.GetByteCount(instructions ?? string.Empty) > _runOptions.MaxInstructionsUtf8Bytes)
        {
            return McpToolResults.Failure(McpExecutionFailureCodes.TaskTooLarge,
                $"Cannot run: instructions exceed the {_runOptions.MaxInstructionsUtf8Bytes / 1024} KiB UTF-8 bound.");
        }

        if (string.IsNullOrWhiteSpace(agent) == string.IsNullOrWhiteSpace(model))
        {
            return McpToolResults.Failure(InvalidRequestCode, "Cannot run: provide exactly one of agent or model.");
        }

        Guid? workspaceId = null;
        if (!string.IsNullOrWhiteSpace(workspace_id))
        {
            if (!Guid.TryParse(workspace_id, out var parsedWorkspaceId) || parsedWorkspaceId == Guid.Empty)
            {
                return McpToolResults.Failure(McpExecutionFailureCodes.WorkspaceNotAuthorized, "Cannot run: the selected workspace is not authorized.");
            }

            workspaceId = parsedWorkspaceId;
        }

        // A local model can take well over a minute to load and generate, and an MCP client aborts a call producing neither a response
        // nor a progress notification inside its idle window, so this early report is what keeps a cold-start run from being killed.
        progress.Report(new ProgressNotificationValue
        {
            Progress = 0f,
            Message = "Admitting the run on the local node…"
        });

        // The fan-out and cloud-spawn caps hang off a per-root-invocation SpawnContext that a chat turn seeds and an MCP call has no
        // equivalent of, so one synthetic root per call bounds an MCP-driven run by exactly the caps an operator-driven one has.
        using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);

        var request = new McpExecutionBindingRequest
        {
            AgentKey = string.IsNullOrWhiteSpace(agent) ? null : agent,
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model,
            ModelOverrideId = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride,
            Instructions = instructions,
            InboundContext = inboundContext,
            ExecutionRequestId = Guid.NewGuid()
        };

        progress.Report(new ProgressNotificationValue
        {
            Progress = 0.1f,
            Message = "Running on the local model…"
        });

        // The inbound execution service returns a typed, sanitized outcome for every EXPECTED rejection — over-cap, no fit, busy,
        // unresolved agent or model — so those reach the caller as an isError tool result and only a real fault becomes a protocol error.
        SpawnOutcome outcome;
        using (var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var heartbeat = ReportHeartbeatAsync(progress, heartbeatStop.Token);
            try
            {
                outcome = await _mcpAgentExecutionService.SpawnForMcpAsync(request,
                    task,
                    expectedBindingFingerprint: null,
                    cancellationToken,
                    workspaceId);
            }
            finally
            {
                await heartbeatStop.CancelAsync();
                await heartbeat;
            }
        }

        // The final progress tells the truth: a declined or failed run never reports "Completed." (I-D14).
        progress.Report(new ProgressNotificationValue
        {
            Progress = 1f,
            Message = outcome.Kind == SpawnOutcomeKind.Success ? "Completed." : outcome.DisplayMessage
        });

        if (outcome.Kind != SpawnOutcomeKind.Success)
        {
            return McpToolResults.Failure(outcome.FailureCode ?? McpExecutionFailureCodes.InternalFailure, outcome.DisplayMessage);
        }

        var result = outcome.Content ?? string.Empty;
        if (result.Length > _runOptions.MaxResultCharacters)
        {
            _logger.LogInformation("An MCP run_agent result was truncated from {ActualLength} to {MaxLength} characters before returning it to the client.",
                result.Length,
                _runOptions.MaxResultCharacters);
            result = string.Concat(result.AsSpan(0, _runOptions.MaxResultCharacters), TruncationMarker);
        }

        return McpToolResults.Text(result);
    }

    // Progress must increase with every notification, so the heartbeat climbs towards, but never reaches, completion.
    private async Task ReportHeartbeatAsync(IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, _timeProvider);
        var beats = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                beats++;
                progress.Report(new ProgressNotificationValue
                {
                    Progress = 0.1f + (0.8f * beats / (beats + 4)),
                    Message = $"Still running on the local model ({beats * (int)HeartbeatInterval.TotalSeconds} s)…"
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run finished (or the call was cancelled); the heartbeat simply stops.
        }
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
        new()
        {
            Status = "rejected",
            Run = null,
            FailureCode = failureCode,
            DisplayMessage = displayMessage
        };

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

    private static string ToWireKind(ModelKind kind) =>
        kind switch
        {
            ModelKind.Chat => "chat",
            ModelKind.Embedding => "embedding",
            ModelKind.Reranker => "reranker",
            ModelKind.Draft => "draft",
            _ => "chat"
        };

    /// <summary>One saved agent, as offered to an external MCP client. Ids are stringified for a JSON-schema-friendly shape.</summary>
    public sealed class AgentSummary
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string? Description { get; init; }
    }

    /// <summary>One available local model, including the node-default marker used by external agents during setup.</summary>
    public sealed class LocalModelSummary
    {
        public required string Name { get; init; }

        public required long? SizeBytes { get; init; }

        public required string Kind { get; init; }

        public required bool IsDefault { get; init; }
    }
}
