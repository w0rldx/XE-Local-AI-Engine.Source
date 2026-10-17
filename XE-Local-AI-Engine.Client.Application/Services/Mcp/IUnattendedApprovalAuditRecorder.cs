namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>Strict, fail-closed audit writer for tool invocations approved by policy rather than by a person.</summary>
/// <remarks>
///     Two callers, one mechanism (ADR 0006, amended by ADR 0021): agentic MCP tool calls, sourced
///     <see cref="McpAgentic" />, and graph action Tool nodes, sourced <see cref="GraphAction" />. A failed write
///     propagates, so the tool never runs unaudited.
/// </remarks>
public interface IUnattendedApprovalAuditRecorder
{
    Task RecordAsync(Guid requestId,
        string toolName,
        ToolCategory category,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>The audit source of an agentic MCP call under the key with <paramref name="keyPrefix" />.</summary>
    static string McpAgentic(string keyPrefix) =>
        $"mcp-agentic:{keyPrefix}";

    /// <summary>The audit source of a graph action Tool node in the definition <paramref name="definitionId" />.</summary>
    static string GraphAction(Guid definitionId) =>
        $"graph-action:{definitionId:D}";
}
