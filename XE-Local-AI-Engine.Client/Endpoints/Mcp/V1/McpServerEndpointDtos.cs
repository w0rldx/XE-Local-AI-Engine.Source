namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Create request for an MCP server registration. The editable fields mirror <see cref="McpServerInput" /> minus
///     <c>Enabled</c>: a registration is always persisted disabled (enabling is the dedicated PATCH below), so the create
///     body carries no enabled flag.
/// </summary>
public sealed class CreateMcpServerRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public McpTransportKind TransportKind { get; init; } = McpTransportKind.Stdio;

    public string? Command { get; init; }

    public IReadOnlyList<string>? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? Url { get; init; }
}

/// <summary>Update request for an MCP server. The id travels in the route; the body carries the new field values (no enabled).</summary>
public sealed class UpdateMcpServerRequest
{
    public Guid McpServerId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public McpTransportKind TransportKind { get; init; } = McpTransportKind.Stdio;

    public string? Command { get; init; }

    public IReadOnlyList<string>? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? Url { get; init; }
}

public sealed class GetMcpServerRequest
{
    public Guid McpServerId { get; init; }
}

public sealed class DeleteMcpServerRequest
{
    public Guid McpServerId { get; init; }
}

public sealed class GetMcpServerToolsRequest
{
    public Guid McpServerId { get; init; }
}

/// <summary>Enable/disable toggle. The id travels in the route; the body carries the new enabled state.</summary>
public sealed class SetMcpServerEnabledRequest
{
    public Guid McpServerId { get; init; }

    public bool Enabled { get; init; }
}

/// <summary>
///     Wire projection of a stored MCP server registration. <see cref="TransportKind" /> serializes as the string
///     "Stdio"/"Http" via the globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize
///     camelCase. The secret-bearing fields (description, arguments, env) are returned decrypted.
/// </summary>
public sealed class McpServerResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required McpTransportKind TransportKind { get; init; }

    public string? Command { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public required IReadOnlyDictionary<string, string> Env { get; init; }

    public string? Url { get; init; }

    public required bool Enabled { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListMcpServersResponse
{
    public required IReadOnlyList<McpServerResponse> Items { get; init; }
}

/// <summary>
///     Live connection state plus discovered tools for one MCP server. <see cref="Status" /> is "connected" (the server
///     is connected and its tools were listed), "disabled" (the server is not enabled, so it is not connected), or
///     "error" (the last connect/list attempt failed; <see cref="Error" /> carries a short redacted reason). The tools
///     list is the discovered set when connected and empty otherwise.
/// </summary>
public sealed class McpServerToolsResponse
{
    public required string Status { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<McpDiscoveredToolResponse> Tools { get; init; }
}

public sealed class McpDiscoveredToolResponse
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required bool RequiresApproval { get; init; }
}

/// <summary>
///     The node's full dynamic tool catalog: built-in tools plus every enabled MCP tool. <see cref="ToolCatalogEntryResponse.Source" />
///     is "builtin" for in-process tools and "mcp:{serverSlug}" for a tool discovered from a registered MCP server, so the
///     UI can group/badge tools by their originating server. This is the single source the React tool pickers consume.
/// </summary>
public sealed class ToolCatalogResponse
{
    public required IReadOnlyList<ToolCatalogEntryResponse> Tools { get; init; }
}

public sealed class ToolCatalogEntryResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool RequiresApproval { get; init; }

    public required string Source { get; init; }
}
