namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;

using System.Diagnostics;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.WebAccess;

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
            Env = Mask(record.Environment),
            // A pre-headers row may carry ?token= in its URL: masked on the wire, restored on update when sent back unchanged.
            Url = McpUrlMask.Mask(record.Url),
            TrustTier = record.TrustTier,
            Headers = Mask(record.Headers),
            SessionScope = record.SessionScope,
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
        return new McpServerInput
        {
            Name = request.Name ?? string.Empty,
            Description = request.Description,
            TransportKind = request.TransportKind,
            Command = request.Command,
            Arguments = request.Arguments ?? [],
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Url = request.Url,
            TrustTier = request.TrustTier,
            Headers = request.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            SessionScope = request.SessionScope,
            Enabled = false
        };
    }

    public static McpServerInput ToInput(this UpdateMcpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The service preserves the current enabled state on update (enabling is the dedicated PATCH), so the value here
        // is a placeholder the service overrides.
        return new McpServerInput
        {
            Name = request.Name ?? string.Empty,
            Description = request.Description,
            TransportKind = request.TransportKind,
            Command = request.Command,
            Arguments = request.Arguments ?? [],
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Url = request.Url,
            TrustTier = request.TrustTier,
            Headers = request.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            SessionScope = request.SessionScope,
            Enabled = false
        };
    }

    // The node's tool-approval policy is reused rather than reimplemented to compute each entry's effective approval, so the badge an operator sees matches the floor the
    // runtime enforcement applies; the application-layer catalog service is the door to that policy. Category travels as its enum name.
    public static ToolCatalogEntryResponse ToResponse(this LocalToolCatalogEntry entry, ToolCatalogService toolCatalog)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(toolCatalog);

        var effectiveRequiresApproval = toolCatalog.RequiresApproval(entry);

        // Matched on the names ToolApprovalCoordinator.IsUserQuestionRequest and WebAccessToolCatalog.IsWebTool match on. These arms come FIRST: ask_user and the web tools are
        // approval-gated too, yet an unattended run continues past them (no answer, no web content), where the approval arm would claim the run fails.
        var unattendedBehaviour = entry.Name switch
        {
            AskUserTool.ToolName => ToolUnattendedBehaviourValues.ContinuesUnanswered,
            _ when WebAccessToolCatalog.IsWebTool(entry.Name) => ToolUnattendedBehaviourValues.ContinuesUnanswered,
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
            SessionScopeEligible = toolCatalog.IsSessionScopeEligible(entry),
            UnattendedBehaviour = unattendedBehaviour
        };
    }

    public static McpServerToolsResponse ToResponse(this McpServerToolsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new McpServerToolsResponse
        {
            Status = view.Status switch
            {
                McpServerToolsStatus.Disabled => "disabled",
                McpServerToolsStatus.Connected => "connected",
                McpServerToolsStatus.Error => "error",
                McpServerToolsStatus.Connecting => "connecting",
                _ => throw new UnreachableException($"Unknown MCP server tools status '{view.Status}'.")
            },
            Error = view.Error,
            FailureReason = view.FailureReason,
            // The qualified name mcp__{serverSlug}__{tool} is the authoritative offered and executable name; the React
            // panel may strip the prefix for display.
            Tools =
            [
                .. view.Tools.Select(static tool => new McpDiscoveredToolResponse
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    RequiresApproval = tool.RequiresApproval
                })
            ]
        };
    }

    private static Dictionary<string, string> Mask(IReadOnlyDictionary<string, string> values)
    {
        return values.ToDictionary(static pair => pair.Key, static _ => McpEnvironmentMask.Value, StringComparer.Ordinal);
    }
}
