namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Mcp;

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

    /// <summary>
    ///     How much of this node the server is trusted with; see <c>docs/security/mcp-trust-tiers.md</c>. Defaults to
    ///     <see cref="McpTrustTier.Sandboxed" />, which is what an omitted value must mean. <c>BuiltInTrusted</c> is
    ///     rejected — it names an engine-owned transport, not a registration. Ignored for an HTTP registration, which
    ///     launches no process.
    /// </summary>
    public McpTrustTier TrustTier { get; init; } = McpTrustTier.Sandboxed;
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

    /// <summary>
    ///     How much of this node the server is trusted with; see <c>docs/security/mcp-trust-tiers.md</c>. Defaults to
    ///     <see cref="McpTrustTier.Sandboxed" />, which is what an omitted value must mean. <c>BuiltInTrusted</c> is
    ///     rejected — it names an engine-owned transport, not a registration. Ignored for an HTTP registration, which
    ///     launches no process.
    /// </summary>
    public McpTrustTier TrustTier { get; init; } = McpTrustTier.Sandboxed;
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
///     Wire projection of a stored MCP server registration. <see cref="TransportKind" /> and <see cref="TrustTier" />
///     serialize as their string names ("Stdio"/"Http", "Sandboxed"/"PrivilegedHost"/"BuiltInTrusted") via the
///     globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize camelCase.
///     <para>
///         Description and arguments are returned decrypted — they are operator-authored text the settings form has to
///         round-trip. <see cref="Env" /> is NOT: it is returned masked, because an environment map is where a stdio
///         server's API keys live and there is no editing reason to read one back.
///     </para>
/// </summary>
public sealed class McpServerResponse
{
    /// <summary>
    ///     The placeholder every <see cref="Env" /> value carries. An update that sends it back for a key keeps that
    ///     key's stored value, which is what lets the settings form round-trip an environment it was never shown.
    /// </summary>
    public const string MaskedEnvironmentValue = McpEnvironmentMask.Value;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required McpTransportKind TransportKind { get; init; }

    public string? Command { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    ///     The configured environment variable NAMES, each carrying <see cref="MaskedEnvironmentValue" /> in place of
    ///     its value. A stdio server's environment is where its API keys live; it is encrypted at rest and never
    ///     travels back out of the node. Sending a masked value back on an update keeps the stored one.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Env { get; init; }

    public string? Url { get; init; }

    /// <summary>How much of this node the server is trusted with; see <c>docs/security/mcp-trust-tiers.md</c>.</summary>
    public required McpTrustTier TrustTier { get; init; }

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

    /// <summary>
    ///     The tool's risk class as the <c>ToolCategory</c> name ("ReadLocal" / "WriteExecute" /
    ///     "Orchestration" / "Network" / "Unknown"). Serialized as a string (matching the <see cref="Source" /> idiom) so
    ///     the UI can label the tool's class; an unrecognized value degrades to fail-closed on the client.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    ///     Whether the tool requires an approval round-trip under the CURRENT node-default approval policy, computed
    ///     through the same <c>IToolApprovalPolicy</c> the runtime enforcement uses (node-default, agent-independent — a
    ///     bound agent may tighten further via its own per-tool overrides). It is the floor an operator sees for the tool.
    /// </summary>
    public required bool EffectiveRequiresApproval { get; init; }

    /// <summary>
    ///     Whether an "approve for this session" decision on this tool can actually be REMEMBERED by the node, computed
    ///     through the same <c>SessionApprovalEligibility</c> predicate the invocation runner's memo uses. The chat
    ///     approval card hides the session button when this is <see langword="false" />, so it never promises a durable
    ///     decision the node will quietly downgrade to a one-shot approval. It is an upper bound, not a guarantee: the
    ///     runner applies further per-CALL narrowings the catalog cannot see (an imported skill, a skill the package does
    ///     not carry, a resource-read that names no resource) which only ever remove eligibility.
    /// </summary>
    public required bool SessionScopeEligible { get; init; }

    /// <summary>
    ///     What reaching this tool does to a run that has NO operator behind it — a scheduled run, an integration
    ///     trigger run, any invocation whose package is unattended. One of the
    ///     <see cref="ToolUnattendedBehaviourValues" />, and NOT derivable from
    ///     <see cref="EffectiveRequiresApproval" />: <c>ask_user</c> is approval-gated too (that is how the call is
    ///     routed to the human round-trip) but an unattended run continues past it, so a warning driven off the
    ///     approval flag alone names a tool that would not actually fail the run.
    /// </summary>
    public required string UnattendedBehaviour { get; init; }
}

/// <summary>
///     The closed value set of <see cref="ToolCatalogEntryResponse.UnattendedBehaviour" />, each read straight off
///     <c>ToolApprovalCoordinator</c>'s two unattended branches. Strings rather than an enum, matching the
///     <see cref="ToolCatalogEntryResponse.Source" /> and <see cref="ToolCatalogEntryResponse.Category" /> idiom on the
///     same record; an unrecognized value must degrade fail-closed on the client.
/// </summary>
public static class ToolUnattendedBehaviourValues
{
    /// <summary>
    ///     The run ENDS. <c>ToolApprovalCoordinator.RequestToolApprovalAsync</c> throws
    ///     <c>ApprovalUnavailableException</c> before anything is broadcast, because executing a tool nobody sanctioned
    ///     is not a safe default.
    /// </summary>
    public const string Fails = "fails";

    /// <summary>
    ///     The run CONTINUES without an answer. <c>ToolApprovalCoordinator.RequestUserAnswerAsync</c> skips the park for
    ///     an unattended package and stashes the same "not answered" result the wait would have reached, so the model
    ///     gets a branchable result instead of a dead turn. Only <c>ask_user</c> behaves this way.
    /// </summary>
    public const string ContinuesUnanswered = "continuesUnanswered";

    /// <summary>The tool needs no human at all, so an unattended run executes it exactly as an interactive one does.</summary>
    public const string Runs = "runs";
}
