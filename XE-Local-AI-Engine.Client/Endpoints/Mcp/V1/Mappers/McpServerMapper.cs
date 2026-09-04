namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Mcp;

internal static class McpServerMapper
{
    public static McpServerResponse ToResponse(this McpServerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new McpServerResponse
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            TransportKind = record.TransportKind,
            Command = record.Command,
            Arguments = record.Arguments,
            WorkingDirectory = record.WorkingDirectory,
            // Keys only. The form renders one row per key and submits the mask back for any value it did not change,
            // so the secret never leaves the node and the round-trip still works.
            Env = record.Environment.ToDictionary(static pair => pair.Key,
                static _ => McpEnvironmentMask.Value,
                StringComparer.Ordinal),
            Url = record.Url,
            TrustTier = record.TrustTier,
            Enabled = record.Enabled,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static McpServerInput ToInput(this CreateMcpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Enabled is always false on create: a registration is persisted disabled and the store ignores this flag, but
        // pass false explicitly so the input is unambiguous.
        return new McpServerInput(request.Name ?? string.Empty,
            request.Description,
            request.TransportKind,
            request.Command,
            request.Arguments ?? [],
            request.WorkingDirectory,
            request.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            request.Url,
            request.TrustTier,
            Enabled: false);
    }

    public static McpServerInput ToInput(this UpdateMcpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The service preserves the current enabled state on update (enabling is the dedicated PATCH), so the value here
        // is a placeholder the service overrides.
        return new McpServerInput(request.Name ?? string.Empty,
            request.Description,
            request.TransportKind,
            request.Command,
            request.Arguments ?? [],
            request.WorkingDirectory,
            request.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            request.Url,
            request.TrustTier,
            Enabled: false);
    }

    // The node's tool-approval policy is reused (not reimplemented) to compute each entry's effective approval, so the
    // badge an operator sees matches the floor the runtime enforcement applies. Category travels as its enum name.
    public static ToolCatalogEntryResponse ToResponse(this LocalToolCatalogEntry entry, IToolApprovalPolicy approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(approvalPolicy);

        var effectiveRequiresApproval = approvalPolicy.RequiresApproval(entry.Name, entry.Category, entry.RequiresApproval);

        // Matched on AskUserTool.ToolName, the same constant ToolApprovalCoordinator.IsUserQuestionRequest matches on,
        // rather than a second list here that could drift from the branch it describes. The question arm comes FIRST:
        // ask_user is approval-gated too, and the approval arm would otherwise claim it fails an unattended run.
        var unattendedBehaviour = entry.Name switch
        {
            AskUserTool.ToolName => ToolUnattendedBehaviourValues.ContinuesUnanswered,
            _ when effectiveRequiresApproval => ToolUnattendedBehaviourValues.Fails,
            _ => ToolUnattendedBehaviourValues.Runs
        };

        return new ToolCatalogEntryResponse
        {
            Name = entry.Name,
            Description = entry.Description,
            RequiresApproval = entry.RequiresApproval,
            Source = entry.Source,
            Category = entry.Category.ToString(),
            EffectiveRequiresApproval = effectiveRequiresApproval,
            SessionScopeEligible = SessionApprovalEligibility.IsToolEligible(approvalPolicy, entry.Name, entry.IsFixedCustomTool),
            UnattendedBehaviour = unattendedBehaviour
        };
    }
}
