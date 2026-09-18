namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>Node-administration tools visible only to trusted agentic MCP credentials.</summary>
[McpServerToolType]
[Authorize(Policy = NodeAuthorizationPolicies.McpAgentic)]
public sealed partial class NodeAdminMcpTools
{
    private static readonly EventId McpAdminToolInvokedEvent = new(4801, "McpAdminToolInvoked");

    /// <summary>
    ///     What the two observe tools answer when the feature is switched off. MCP has no path-prefix gate to hide
    ///     behind the way the REST module does, so the tools stay registered and say so rather than faulting.
    /// </summary>
    private const string DevWorkflowsDisabledMessage = "Development workflows are disabled on this node.";

    private readonly IAgentDefinitionService _agentDefinitionService;
    private readonly IDevWorkflowRunService _devWorkflowRunService;
    private readonly IDevWorkflowStore _devWorkflowStore;
    private readonly DevWorkflowOptions _devWorkflowOptions;
    private readonly IGgufDownloadCoordinator _ggufDownloadCoordinator;
    private readonly ILocalModelAdministrationService _localModelAdministrationService;
    private readonly INodeSettingsAdministrationService _nodeSettingsAdministrationService;
    private readonly ILlamaCppRuntimeAdministrationService _runtimeAdministrationService;
    private readonly TimeProvider _timeProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<NodeAdminMcpTools> _logger;

    public NodeAdminMcpTools(
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
        ArgumentNullException.ThrowIfNull(agentDefinitionService);
        _agentDefinitionService = agentDefinitionService;
        ArgumentNullException.ThrowIfNull(devWorkflowRunService);
        _devWorkflowRunService = devWorkflowRunService;
        ArgumentNullException.ThrowIfNull(devWorkflowStore);
        _devWorkflowStore = devWorkflowStore;
        _devWorkflowOptions = (devWorkflowOptions ?? throw new ArgumentNullException(nameof(devWorkflowOptions))).Value;
        ArgumentNullException.ThrowIfNull(ggufDownloadCoordinator);
        _ggufDownloadCoordinator = ggufDownloadCoordinator;
        ArgumentNullException.ThrowIfNull(localModelAdministrationService);
        _localModelAdministrationService = localModelAdministrationService;
        ArgumentNullException.ThrowIfNull(nodeSettingsAdministrationService);
        _nodeSettingsAdministrationService = nodeSettingsAdministrationService;
        ArgumentNullException.ThrowIfNull(runtimeAdministrationService);
        _runtimeAdministrationService = runtimeAdministrationService;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _httpContextAccessor = httpContextAccessor;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

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
            var result = await invoke();
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
                ? await _agentDefinitionService.CreateAsync(input, cancellationToken)
                : await _agentDefinitionService.UpdateAsync(id.Value, input, cancellationToken);
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
