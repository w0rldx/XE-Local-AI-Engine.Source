namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

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
            Env = record.Environment,
            Url = record.Url,
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
            Enabled: false);
    }

    public static ToolCatalogEntryResponse ToResponse(this LocalToolCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ToolCatalogEntryResponse
        {
            Name = entry.Name,
            Description = entry.Description,
            RequiresApproval = entry.RequiresApproval,
            Source = entry.Source
        };
    }
}
