namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>Node-administration tools visible only to trusted agentic MCP credentials.</summary>
[McpServerToolType]
[Authorize(Policy = NodeAuthorizationPolicies.McpAgentic)]
public sealed class NodeAdminMcpTools(
    ILlamaCppRuntimeAdministrationService runtimeAdministrationService,
    IGgufDownloadCoordinator ggufDownloadCoordinator,
    ILocalModelAdministrationService localModelAdministrationService,
    INodeSettingsAdministrationService nodeSettingsAdministrationService,
    IAgentDefinitionService agentDefinitionService,
    IDevWorkflowRunService devWorkflowRunService,
    IDevWorkflowStore devWorkflowStore,
    IOptions<DevWorkflowOptions> devWorkflowOptions,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor,
    ILogger<NodeAdminMcpTools> logger)
{
    private static readonly EventId McpAdminToolInvokedEvent = new(4801, "McpAdminToolInvoked");

    /// <summary>
    ///     What the two observe tools answer when the feature is switched off. MCP has no path-prefix gate to hide
    ///     behind the way the REST module does, so the tools stay registered and say so rather than faulting.
    /// </summary>
    private const string DevWorkflowsDisabledMessage = "Development workflows are disabled on this node.";

    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));
    private readonly IDevWorkflowRunService _devWorkflowRunService = devWorkflowRunService ?? throw new ArgumentNullException(nameof(devWorkflowRunService));
    private readonly IDevWorkflowStore _devWorkflowStore = devWorkflowStore ?? throw new ArgumentNullException(nameof(devWorkflowStore));
    private readonly DevWorkflowOptions _devWorkflowOptions = (devWorkflowOptions ?? throw new ArgumentNullException(nameof(devWorkflowOptions))).Value;
    private readonly IGgufDownloadCoordinator _ggufDownloadCoordinator = ggufDownloadCoordinator ?? throw new ArgumentNullException(nameof(ggufDownloadCoordinator));
    private readonly ILocalModelAdministrationService _localModelAdministrationService = localModelAdministrationService ?? throw new ArgumentNullException(nameof(localModelAdministrationService));

    private readonly INodeSettingsAdministrationService _nodeSettingsAdministrationService =
        nodeSettingsAdministrationService ?? throw new ArgumentNullException(nameof(nodeSettingsAdministrationService));

    private readonly ILlamaCppRuntimeAdministrationService _runtimeAdministrationService = runtimeAdministrationService ?? throw new ArgumentNullException(nameof(runtimeAdministrationService));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly ILogger<NodeAdminMcpTools> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [McpServerTool(Name = "get_status")]
    [Description("Get the node version, uptime, selected default model, and number of loaded llama.cpp processes.")]
    public Task<McpNodeStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_status", [], async () =>
        {
            var settings = await _nodeSettingsAdministrationService.GetAgenticViewAsync(cancellationToken).ConfigureAwait(false);
            var runtime = await _runtimeAdministrationService.GetStatusAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            return new McpNodeStatusResponse(GetVersion(), GetProcessUptimeSeconds(), settings.DefaultModelName, runtime.RunningProcessCount);
        });

    [McpServerTool(Name = "get_runtime_status")]
    [Description("Get the installed and recommended llama.cpp runtime versions without refreshing the remote catalog.")]
    public Task<McpRuntimeStatusResponse> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_runtime_status", [], async () =>
        {
            var status = await _runtimeAdministrationService.GetStatusAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            return new McpRuntimeStatusResponse(status.Installed?.Tag,
                status.RecommendedTag,
                status.UpstreamLatestTag,
                status.UpdateAvailable,
                status.IsOffline,
                status.RunningProcessCount);
        });

    [McpServerTool(Name = "start_runtime_acquisition")]
    [Description("Start acquiring the managed llama.cpp runtime. Omit variant to select the best local backend automatically.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpRuntimeAcquisitionStartResponse> StartRuntimeAcquisitionAsync(CancellationToken cancellationToken,
        [Description("Optional backend: cpu, cuda, or vulkan.")]
        string? variant = null)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("start_runtime_acquisition", AuditArguments(("variant", variant)), async () =>
        {
            if (!TryParseVariant(variant, out var parsedVariant))
            {
                return new McpRuntimeAcquisitionStartResponse("rejected", null, McpAdminToolFailureCodes.InvalidVariant,
                    "Variant must be cpu, cuda, vulkan, or omitted.");
            }

            var result = await _runtimeAdministrationService.StartAcquisitionAsync(parsedVariant, cancellationToken).ConfigureAwait(false);
            return result.Accepted
                ? new McpRuntimeAcquisitionStartResponse("accepted", result.Variant)
                : new McpRuntimeAcquisitionStartResponse("busy", result.Variant, McpAdminToolFailureCodes.Busy, result.DisplayMessage);
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_runtime_acquisition")]
    [Description("Get the current or most recent sanitized llama.cpp runtime acquisition progress.")]
    public Task<McpRuntimeAcquisitionResponse> GetRuntimeAcquisition() =>
        InvokeAuditedAsync("get_runtime_acquisition", [], () =>
            Task.FromResult(_runtimeAdministrationService.GetAcquisitionStatus().ToResponse()));

    [McpServerTool(Name = "start_model_pull")]
    [Description("Start or rejoin a background GGUF model pull from Hugging Face.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpModelPullStartResponse> StartModelPullAsync([Description("Hugging Face repository id.")] string repo_id,
        CancellationToken cancellationToken,
        [Description("Optional exact GGUF file name.")]
        string? file_name = null,
        [Description("Optional quant label when file_name is omitted.")]
        string? quant = null,
        [Description("Optional branch, tag, or commit revision.")]
        string? revision = null)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("start_model_pull",
            AuditArguments(("repo_id", repo_id), ("file_name", file_name), ("quant", quant), ("revision", revision)),
            async () =>
            {
                if (string.IsNullOrWhiteSpace(repo_id))
                {
                    return new McpModelPullStartResponse("rejected", null, null, McpAdminToolFailureCodes.InvalidRequest,
                        "A repository id is required.");
                }

                try
                {
                    var ticket = await _ggufDownloadCoordinator.StartAsync(new GgufModelRequest
                    {
                        RepoId = repo_id.Trim(),
                        FileName = NullIfWhiteSpace(file_name),
                        Quant = NullIfWhiteSpace(quant),
                        Revision = NullIfWhiteSpace(revision)
                    }, cancellationToken).ConfigureAwait(false);
                    return new McpModelPullStartResponse(ticket.AlreadyInFlight ? "already_in_flight" : "accepted",
                        ticket.ModelName,
                        ticket.OperationId == Guid.Empty ? null : ticket.OperationId.ToString("D"));
                }
                catch (GgufAcquisitionConflictException exception)
                {
                    return new McpModelPullStartResponse("rejected", null, null, McpAdminToolFailureCodes.ModelPullConflict, exception.Message);
                }
                catch (HuggingFaceDownloadException exception)
                {
                    return new McpModelPullStartResponse("rejected",
                        null,
                        null,
                        McpAdminWireNames.DownloadErrorCode(exception.Reason),
                        exception.Message);
                }
            },
            static response => response.FailureCode is not null).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_model_pull")]
    [Description("Poll a background GGUF model pull by canonical model name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpModelPullResponse> GetModelPull([Description("Canonical model name returned by start_model_pull.")] string model_name)
#pragma warning restore IDE1006
    {
        return InvokeAuditedAsync("get_model_pull", AuditArguments(("model_name", model_name)), () =>
        {
            if (string.IsNullOrWhiteSpace(model_name))
            {
                return Task.FromResult(new McpModelPullResponse("not_found", null, null, null, null, null, null,
                    McpAdminToolFailureCodes.InvalidRequest, "A model name is required."));
            }

            var status = _ggufDownloadCoordinator.GetStatus(model_name.Trim());
            if (status is null)
            {
                return Task.FromResult(new McpModelPullResponse("not_found", null, null, null, null, null, null,
                    McpAdminToolFailureCodes.ModelPullNotFound, "Model pull not found."));
            }

            var operationId = status.OperationId == Guid.Empty ? null : status.OperationId.ToString("D");
            return Task.FromResult(new McpModelPullResponse("ok",
                status.ModelName,
                ToWirePhase(status.Phase),
                status.CompletedBytes,
                status.TotalBytes,
                status.SanitizedError,
                operationId,
                status.Phase == GgufDownloadPhase.Failed ? McpAdminWireNames.DownloadErrorCode(status.ErrorCode) : null,
                status.SanitizedError));
        }, static response => response.FailureCode is not null);
    }

    [McpServerTool(Name = "cancel_model_pull")]
    [Description("Request cooperative cancellation of a background GGUF model pull.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpModelPullCancelResponse> CancelModelPull([Description("Canonical model name returned by start_model_pull.")] string model_name) =>
        InvokeAuditedAsync("cancel_model_pull", AuditArguments(("model_name", model_name)), () =>
                Task.FromResult(new McpModelPullCancelResponse(!string.IsNullOrWhiteSpace(model_name)
                                                               && _ggufDownloadCoordinator.Cancel(model_name.Trim()))),
            static response => !response.Cancelled);
#pragma warning restore IDE1006

    [McpServerTool(Name = "delete_model")]
    [Description("Delete a locally installed model through the node's coordinated deletion service.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpModelDeleteResponse> DeleteModelAsync(string model_name, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("delete_model", AuditArguments(("model_name", model_name)), async () =>
        {
            var result = await _localModelAdministrationService.DeleteAsync(model_name, cancellationToken).ConfigureAwait(false);
            return new McpModelDeleteResponse(result.Deleted, result.ModelName, result.FailureCode, result.DisplayMessage);
        }, static response => !response.Deleted).ConfigureAwait(false);
    }

    [McpServerTool(Name = "set_default_model")]
    [Description("Select an installed local model as the node default.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpDefaultModelResponse> SetDefaultModelAsync(string model_name, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("set_default_model", AuditArguments(("model_name", model_name)), async () =>
        {
            var result = await _localModelAdministrationService.SelectDefaultAsync(model_name,
                LocalModelSelectionPolicy.InstalledLocalOnly,
                cancellationToken).ConfigureAwait(false);
            return new McpDefaultModelResponse(result.Succeeded,
                result.SelectedModelName,
                result.PreviousModelName,
                result.FailureCode,
                result.DisplayMessage);
        }, static response => !response.Updated).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_node_settings")]
    [Description("Get only the restricted core node settings available to agentic automation.")]
    public Task<NodeSettingsAgenticView> GetNodeSettingsAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_node_settings", [], () =>
            _nodeSettingsAdministrationService.GetAgenticViewAsync(cancellationToken));

    [McpServerTool(Name = "update_node_settings")]
    [Description("Apply a partial update to the exact restricted 18-field agentic node-settings whitelist.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpNodeSettingsUpdateResponse> UpdateNodeSettingsAsync(CancellationToken cancellationToken,
        string? default_model_name = null,
        bool? enable_tools = null,
        IReadOnlyList<string>? tool_capable_models = null,
        string? hugging_face_default_quant = null,
        int? llama_max_loaded_processes = null,
        int? llama_idle_time_to_live_seconds = null,
        bool? keep_model_warm_enabled = null,
        string? keep_model_warm_model_name = null,
        int? keep_model_warm_interval_seconds = null,
        int? max_message_request_timeout_seconds = null,
        int? chat_cache_reuse = null,
        string? speculative_mode = null,
        string? speculative_draft_model_name = null,
        int? speculative_draft_max_tokens = null,
        int? speculative_draft_gpu_layers = null,
        string? kv_cache_type = null,
        string? reranker_model_name = null,
        string? auto_effort_fast_model_name = null)
#pragma warning restore IDE1006
    {
        var arguments = AuditArguments(("default_model_name", default_model_name),
            ("enable_tools", enable_tools),
            ("tool_capable_models", tool_capable_models),
            ("hugging_face_default_quant", hugging_face_default_quant),
            ("llama_max_loaded_processes", llama_max_loaded_processes),
            ("llama_idle_time_to_live_seconds", llama_idle_time_to_live_seconds),
            ("keep_model_warm_enabled", keep_model_warm_enabled),
            ("keep_model_warm_model_name", keep_model_warm_model_name),
            ("keep_model_warm_interval_seconds", keep_model_warm_interval_seconds),
            ("max_message_request_timeout_seconds", max_message_request_timeout_seconds),
            ("chat_cache_reuse", chat_cache_reuse),
            ("speculative_mode", speculative_mode),
            ("speculative_draft_model_name", speculative_draft_model_name),
            ("speculative_draft_max_tokens", speculative_draft_max_tokens),
            ("speculative_draft_gpu_layers", speculative_draft_gpu_layers),
            ("kv_cache_type", kv_cache_type),
            ("reranker_model_name", reranker_model_name),
            ("auto_effort_fast_model_name", auto_effort_fast_model_name));
        return await InvokeAuditedAsync("update_node_settings", arguments, async () =>
        {
            var result = await _nodeSettingsAdministrationService.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
            {
                DefaultModelName = default_model_name,
                EnableTools = enable_tools,
                ToolCapableModels = tool_capable_models,
                HuggingFaceDefaultQuant = hugging_face_default_quant,
                LlamaMaxLoadedProcesses = llama_max_loaded_processes,
                LlamaIdleTimeToLiveSeconds = llama_idle_time_to_live_seconds,
                KeepModelWarmEnabled = keep_model_warm_enabled,
                KeepModelWarmModelName = keep_model_warm_model_name,
                KeepModelWarmIntervalSeconds = keep_model_warm_interval_seconds,
                MaxMessageRequestTimeoutSeconds = max_message_request_timeout_seconds,
                ChatCacheReuse = chat_cache_reuse,
                SpeculativeMode = speculative_mode,
                SpeculativeDraftModelName = speculative_draft_model_name,
                SpeculativeDraftMaxTokens = speculative_draft_max_tokens,
                SpeculativeDraftGpuLayers = speculative_draft_gpu_layers,
                KvCacheType = kv_cache_type,
                RerankerModelName = reranker_model_name,
                AutoEffortFastModelName = auto_effort_fast_model_name
            }, cancellationToken).ConfigureAwait(false);
            return result.ToResponse();
        }, static response => !response.Updated).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_agent")]
    [Description("Get one saved agent by id or exact name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> GetAgentAsync(string agent_id, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("get_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var record = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            return record is null
                ? AgentNotFound()
                : new McpAgentResponse("ok", McpAgentDefinition.FromRecord(record));
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }

    [McpServerTool(Name = "create_agent")]
    [Description("Validate and create a saved agent through the same application service used by the operator API.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> CreateAgentAsync(string name,
        string instructions,
        CancellationToken cancellationToken,
        string? description = null,
        string? model_profile = null,
        string? reasoning_effort = null,
        string kind = "single",
        IReadOnlyList<string>? allowed_tool_names = null,
        IReadOnlyDictionary<string, bool>? tool_approvals = null,
        string? orchestration_topology_json = null,
        bool playbook_enabled = false,
        IReadOnlyList<string>? allowed_skill_ids = null,
        bool default_temporary_chat = false,
        bool memory_extraction_enabled = true,
        bool disable_base_scaffold = false,
        McpGenerationMetadataInput? generation_metadata = null) =>
        await InvokeAuditedAsync("create_agent",
            AgentAuditArguments(name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                allowed_skill_ids,
                playbook_enabled,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata),
            () => SaveAgentAsync(id: null,
                name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                playbook_enabled,
                allowed_skill_ids,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata,
                cancellationToken),
            static response => response.FailureCode is not null).ConfigureAwait(false);
#pragma warning restore IDE1006

    [McpServerTool(Name = "update_agent")]
    [Description("Fully replace an existing saved agent by id or exact name through the shared validation service.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> UpdateAgentAsync(string agent_id,
        string name,
        string instructions,
        CancellationToken cancellationToken,
        string? description = null,
        string? model_profile = null,
        string? reasoning_effort = null,
        string kind = "single",
        IReadOnlyList<string>? allowed_tool_names = null,
        IReadOnlyDictionary<string, bool>? tool_approvals = null,
        string? orchestration_topology_json = null,
        bool playbook_enabled = false,
        IReadOnlyList<string>? allowed_skill_ids = null,
        bool default_temporary_chat = false,
        bool memory_extraction_enabled = true,
        bool disable_base_scaffold = false,
        McpGenerationMetadataInput? generation_metadata = null)
    {
        var arguments = AgentAuditArguments(name,
            instructions,
            description,
            model_profile,
            reasoning_effort,
            kind,
            allowed_tool_names,
            tool_approvals,
            orchestration_topology_json,
            allowed_skill_ids,
            playbook_enabled,
            default_temporary_chat,
            memory_extraction_enabled,
            disable_base_scaffold,
            generation_metadata,
            ("agent_id", agent_id));
        return await InvokeAuditedAsync("update_agent", arguments, async () =>
        {
            var existing = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return AgentNotFound();
            }

            return await SaveAgentAsync(existing.Id,
                name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                playbook_enabled,
                allowed_skill_ids,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata,
                cancellationToken).ConfigureAwait(false);
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }
#pragma warning restore IDE1006

    [McpServerTool(Name = "delete_agent")]
    [Description("Delete a saved agent by id or exact name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentDeleteResponse> DeleteAgentAsync(string agent_id, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("delete_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var existing = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new McpAgentDeleteResponse(false, McpAdminToolFailureCodes.AgentNotFound, "Agent not found.");
            }

            var deleted = await _agentDefinitionService.DeleteAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            return deleted
                ? new McpAgentDeleteResponse(true)
                : new McpAgentDeleteResponse(false, McpAdminToolFailureCodes.AgentNotFound, "Agent not found.");
        }, static response => !response.Deleted).ConfigureAwait(false);
    }

    [McpServerTool(Name = "list_workflow_runs")]
    [Description(
        "List development workflow runs as bounded lifecycle metadata — one row per work item's LATEST run, matching the operator's own list. Each row carries the run status, node tallies and pending-decision count. An optional case-insensitive run status filter may be supplied. Graphs, artifact contents, work-session transcripts and host paths are never returned, and nothing here starts, cancels or otherwise moves a run.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpWorkflowRunListResponse> ListWorkflowRunsAsync(CancellationToken cancellationToken,
        [Description("Maximum runs to return. Values are clamped to the server's configured bounded range.")]
        int? limit = null,
        [Description("Optional run status: pending, running, pausing, paused, waitingForApproval, cancelling, completed, failed, or cancelled.")]
        string? status = null)
#pragma warning restore CA1707, IDE1006
        =>
            InvokeAuditedAsync("list_workflow_runs", AuditArguments(("limit", limit), ("status", status)), async () =>
            {
                var boundedLimit = ClampWorkflowListLimit(limit);
                if (!_devWorkflowOptions.Enabled)
                {
                    return new McpWorkflowRunListResponse(McpAdminToolFailureCodes.NotAvailable,
                        [],
                        0,
                        boundedLimit,
                        McpAdminToolFailureCodes.NotAvailable,
                        DevWorkflowsDisabledMessage);
                }

                DevWorkflowRunStatus? parsedStatus = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    var canonicalStatus = Enum.GetNames<DevWorkflowRunStatus>()
                                              .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));
                    if (canonicalStatus is null || !Enum.TryParse(canonicalStatus, out DevWorkflowRunStatus value))
                    {
                        return new McpWorkflowRunListResponse(McpAdminToolFailureCodes.InvalidStatus,
                            [],
                            0,
                            boundedLimit,
                            McpAdminToolFailureCodes.InvalidStatus,
                            $"Cannot list: status must be one of {string.Join(", ", Enum.GetNames<DevWorkflowRunStatus>())}.");
                    }

                    parsedStatus = value;
                }

                // The work-item list IS the run list on this surface: a work item carries its latest run's status and
                // node counters, so filtering it needs no second query and says the same thing the operator's page does.
                // ponytail: the store filters by WORK-ITEM status and this tool filters by RUN status, which are
                // different enums, so the status filter and the limit are applied in memory over the whole list. Ceiling
                // is the number of work items on the node; push it down only if a store-side run-status list appears.
                var workItems = await _devWorkflowStore.ListWorkItemsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var runs = workItems.Where(item => item.LatestRunId is not null && item.LatestRunStatus is not null)
                                    .Where(item => parsedStatus is null || item.LatestRunStatus == parsedStatus)
                                    .Take(boundedLimit)
                                    .Select(static item => new McpWorkflowRunSummary(item.LatestRunId!.Value.ToString("D"),
                                        item.Id.ToString("D"),
                                        item.LatestRunDefinitionName,
                                        item.LatestRunStatus!.Value.ToString(),
                                        item.LatestRunNodes.Queued,
                                        item.LatestRunNodes.Running,
                                        item.LatestRunNodes.Completed,
                                        item.LatestRunNodes.Total,
                                        item.LatestRunNodes.PendingDecisionCount))
                                    .ToArray();
                return new McpWorkflowRunListResponse("ok", runs, runs.Length, boundedLimit);
            }, static response => response.FailureCode is not null);

    [McpServerTool(Name = "get_workflow_run")]
    [Description(
        "Get one development workflow run by id: its status, node tallies, pending-decision count, failure class, sanitized terminal reason, start and end timestamps, and one bounded row per node run (key, type, status, attempt, max attempts). The pinned graph, artifact contents, work-session transcripts and host paths are never returned, and nothing here moves the run.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpWorkflowRunGetResponse> GetWorkflowRunAsync([Description("The canonical hyphenated UUID of the run, as returned by list_workflow_runs.")] string run_id,
        CancellationToken cancellationToken)
#pragma warning restore CA1707, IDE1006
        =>
            InvokeAuditedAsync("get_workflow_run", AuditArguments(("run_id", run_id)), async () =>
            {
                if (!_devWorkflowOptions.Enabled)
                {
                    return new McpWorkflowRunGetResponse(McpAdminToolFailureCodes.NotAvailable,
                        null,
                        McpAdminToolFailureCodes.NotAvailable,
                        DevWorkflowsDisabledMessage);
                }

                if (!Guid.TryParseExact(run_id, "D", out var runId) || runId == Guid.Empty)
                {
                    return new McpWorkflowRunGetResponse(McpAdminToolFailureCodes.InvalidRequest,
                        null,
                        McpAdminToolFailureCodes.InvalidRequest,
                        "Cannot get: provide a valid run UUID.");
                }

                DevWorkflowRunDetail detail;
                try
                {
                    detail = await _devWorkflowRunService.GetAsync(runId, cancellationToken).ConfigureAwait(false);
                }
                catch (DevWorkflowNotFoundException)
                {
                    return new McpWorkflowRunGetResponse("not_found", null, McpAdminToolFailureCodes.RunNotFound, "Run not found.");
                }

                // Names only, over the summary projection that never decrypts a graph blob — the same read the run
                // detail endpoint uses to label a run.
                var definitions = await _devWorkflowStore.ListDefinitionsAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
                var run = detail.Run;
                return new McpWorkflowRunGetResponse("ok",
                    new McpWorkflowRunDetail(run.Id.ToString("D"),
                        run.WorkItemId.ToString("D"),
                        definitions.FirstOrDefault(definition => definition.Id == run.DefinitionId)?.Name,
                        run.Status.ToString(),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded),
                        detail.NodeRuns.Count,
                        detail.PendingDecisionCount,
                        run.FailureClass,
                        run.TerminalReason,
                        run.StartedAtUtc,
                        run.EndedAtUtc,
                        [
                            .. detail.NodeRuns.Select(static nodeRun => new McpWorkflowNodeRunSummary(nodeRun.NodeKey,
                                nodeRun.NodeType.ToString(),
                                nodeRun.Status.ToString(),
                                nodeRun.Attempt,
                                nodeRun.MaxAttempts))
                        ]));
            }, static response => response.FailureCode is not null);

    private int ClampWorkflowListLimit(int? limit) =>
        Math.Clamp(limit ?? _devWorkflowOptions.McpDefaultListLimit, 1, _devWorkflowOptions.McpMaxListLimit);

    private async Task<T> InvokeAuditedAsync<T>(string toolName,
        IReadOnlyList<KeyValuePair<string, object?>> arguments,
        Func<Task<T>> invoke,
        Func<T, bool>? isRejected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(invoke);

        var started = Stopwatch.GetTimestamp();
        var keyPrefix = "unattributed";
        var outcome = "rejected";
        try
        {
            var inboundContext = McpInboundExecutionContext.FromPrincipal(_httpContextAccessor.HttpContext?.User);
            if (!inboundContext.IsAgentic || !McpInboundExecutionContext.IsBoundedPrefix(inboundContext.KeyPrefix))
            {
                throw new InvalidOperationException("MCP administration requires authenticated agentic provenance.");
            }

            keyPrefix = inboundContext.KeyPrefix!;
            outcome = "faulted";
            var result = await invoke().ConfigureAwait(false);
            outcome = isRejected?.Invoke(result) == true ? "rejected" : "success";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            TryWriteAuditEvent(toolName, keyPrefix, arguments, outcome, durationMs);
        }
    }

    private void TryWriteAuditEvent(string toolName,
        string keyPrefix,
        IReadOnlyList<KeyValuePair<string, object?>> arguments,
        string outcome,
        long durationMs)
    {
        try
        {
            _logger.LogInformation(McpAdminToolInvokedEvent,
                "McpAdminToolInvoked Tool={Tool} KeyPrefix={KeyPrefix} ArgsSummary={ArgsSummary} Outcome={Outcome} DurationMs={DurationMs}",
                toolName,
                keyPrefix,
                FormatAuditArguments(arguments),
                outcome,
                durationMs);
        }
        catch (Exception)
        {
            // Audit sinks are observational. A sink failure must never turn a completed mutation into a client-visible
            // failure (and retry), or replace the operation's original fault/cancellation with a logging exception.
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> AgentAuditArguments(string name,
        string instructions,
        string? description,
        string? modelProfile,
        string? reasoningEffort,
        string kind,
        IReadOnlyList<string>? allowedToolNames,
        IReadOnlyDictionary<string, bool>? toolApprovals,
        string? orchestrationTopologyJson,
        IReadOnlyList<string>? allowedSkillIds,
        bool playbookEnabled,
        bool defaultTemporaryChat,
        bool memoryExtractionEnabled,
        bool disableBaseScaffold,
        McpGenerationMetadataInput? generationMetadata,
        params (string Name, object? Value)[] additionalArguments) =>
        AuditArguments([
            ("name", name),
            ("instructions", instructions),
            ("description", description),
            ("model_profile", modelProfile),
            ("reasoning_effort", reasoningEffort),
            ("kind", kind),
            ("allowed_tool_names", allowedToolNames),
            ("tool_approvals", toolApprovals),
            ("orchestration_topology_json", orchestrationTopologyJson),
            ("allowed_skill_ids", allowedSkillIds),
            ("playbook_enabled", playbookEnabled),
            ("default_temporary_chat", defaultTemporaryChat),
            ("memory_extraction_enabled", memoryExtractionEnabled),
            ("disable_base_scaffold", disableBaseScaffold),
            ("generation_metadata", generationMetadata),
            .. additionalArguments
        ]);

    private static IReadOnlyList<KeyValuePair<string, object?>> AuditArguments(params (string Name, object? Value)[] arguments) =>
        [.. arguments.Select(static argument => new KeyValuePair<string, object?>(argument.Name, argument.Value))];

    private static string FormatAuditArguments(IReadOnlyList<KeyValuePair<string, object?>> arguments) =>
        arguments.Count == 0
            ? "none"
            : string.Join(',', arguments.Select(static argument => $"{argument.Key}={SummarizeAuditValue(argument.Key, argument.Value)}"));

    private static string SummarizeAuditValue(string name, object? value)
    {
        if (name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted]";
        }

        return value switch
        {
            null => "omitted",
            string text => $"provided(chars:{text.Length})",
            IReadOnlyCollection<string> collection => $"provided(count:{collection.Count})",
            IReadOnlyDictionary<string, bool> dictionary => $"provided(count:{dictionary.Count})",
            McpGenerationMetadataInput => "provided",
            bool boolean => boolean ? "true" : "false",
            int number => number.ToString(CultureInfo.InvariantCulture),
            _ => "provided"
        };
    }

    private async Task<McpAgentResponse> SaveAgentAsync(Guid? id,
        string name,
        string instructions,
        string? description,
        string? modelProfile,
        string? reasoningEffort,
        string kind,
        IReadOnlyList<string>? allowedToolNames,
        IReadOnlyDictionary<string, bool>? toolApprovals,
        string? orchestrationTopologyJson,
        bool playbookEnabled,
        IReadOnlyList<string>? allowedSkillIds,
        bool defaultTemporaryChat,
        bool memoryExtractionEnabled,
        bool disableBaseScaffold,
        McpGenerationMetadataInput? generationMetadata,
        CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var parsedKind) || !TryParseIds(allowedSkillIds, out var parsedSkillIds))
        {
            return new McpAgentResponse("rejected", null, McpAdminToolFailureCodes.ValidationFailed,
                "Agent kind must be single or orchestrator and every skill id must be a UUID.");
        }

        if (!TryMapGenerationMetadata(generationMetadata, out var metadata, out var metadataError))
        {
            return new McpAgentResponse("rejected", null, McpAdminToolFailureCodes.ValidationFailed, metadataError);
        }

        if (GenerationProvenance.Validate(metadata) is { } validationError)
        {
            return new McpAgentResponse("rejected", null, McpAdminToolFailureCodes.ValidationFailed, validationError);
        }

        var input = new AgentDefinitionInput(name,
            description,
            instructions,
            modelProfile,
            reasoningEffort,
            parsedKind,
            allowedToolNames ?? [],
            toolApprovals ?? new Dictionary<string, bool>(StringComparer.Ordinal),
            orchestrationTopologyJson,
            playbookEnabled,
            parsedSkillIds,
            defaultTemporaryChat,
            memoryExtractionEnabled,
            disableBaseScaffold,
            GenerationProvenance.ToPersistedJson(metadata,
                name,
                description,
                instructions,
                _timeProvider.GetUtcNow()));
        try
        {
            var record = id is null
                ? await _agentDefinitionService.CreateAsync(input, cancellationToken).ConfigureAwait(false)
                : await _agentDefinitionService.UpdateAsync(id.Value, input, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return AgentNotFound();
            }

            var status = id is null ? "created" : "updated";
            return new McpAgentResponse(status, McpAgentDefinition.FromRecord(record));
        }
        catch (AgentDefinitionValidationException exception)
        {
            return new McpAgentResponse("rejected", null, McpAdminToolFailureCodes.ValidationFailed, exception.Message);
        }
    }

    private static McpAgentResponse AgentNotFound() =>
        new("not_found", null, McpAdminToolFailureCodes.AgentNotFound, "Agent not found.");

    private static string ToWirePhase(GgufDownloadPhase phase) =>
        phase switch
        {
            GgufDownloadPhase.Running => "running",
            GgufDownloadPhase.Completed => "completed",
            GgufDownloadPhase.Cancelled => "cancelled",
            GgufDownloadPhase.Failed => "failed",
            _ => "failed"
        };

    private static string GetVersion() =>
        typeof(NodeAdminMcpTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(NodeAdminMcpTools).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private long GetProcessUptimeSeconds()
    {
        using var process = Process.GetCurrentProcess();
        var processStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var uptime = _timeProvider.GetUtcNow() - processStart;
        return Math.Max(0, (long)uptime.TotalSeconds);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseIds(IReadOnlyList<string>? values, out IReadOnlyList<Guid> ids)
    {
        if (values is null)
        {
            ids = [];
            return true;
        }

        var parsed = new List<Guid>(values.Count);
        foreach (var value in values)
        {
            if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            {
                ids = [];
                return false;
            }

            parsed.Add(id);
        }

        ids = parsed;
        return true;
    }

    private static bool TryParseKind(string? value, out AgentDefinitionKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static bool TryMapGenerationMetadata(McpGenerationMetadataInput? value,
        out GenerationMetadataInput? metadata,
        out string? error)
    {
        if (value is null)
        {
            metadata = null;
            error = null;
            return true;
        }

        var mode = value.Mode?.Trim().ToUpperInvariant() switch
        {
            "CREATE" => DraftMode.Create,
            "IMPROVE" => DraftMode.Improve,
            _ => (DraftMode?)null
        };
        if (mode is null)
        {
            metadata = null;
            error = "Generation metadata mode must be create or improve.";
            return false;
        }

        metadata = new GenerationMetadataInput(value.Model,
            mode.Value,
            value.UserBrief,
            value.Rationale,
            value.Assumptions,
            value.Confidence,
            value.GeneratedAtUtc,
            value.DraftContentHash);
        error = null;
        return true;
    }

    private static bool TryParseVariant(string? value, out GpuVariant? variant)
    {
        variant = value?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "CPU" => GpuVariant.Cpu,
            "CUDA" => GpuVariant.Cuda,
            "VULKAN" => GpuVariant.Vulkan,
            _ => (GpuVariant?)null
        };
        return string.IsNullOrWhiteSpace(value) || variant is not null;
    }
}
