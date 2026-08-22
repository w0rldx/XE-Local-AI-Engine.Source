namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Persists agentic auto-approval before execution; unlike the human recorder, failures propagate.</summary>
internal sealed class McpAgenticApprovalAuditRecorder(IAgentExecutionLogStore store) : IMcpAgenticApprovalAuditRecorder
{
    private readonly IAgentExecutionLogStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task RecordAsync(Guid requestId,
        string toolName,
        ToolCategory category,
        string keyPrefix,
        CancellationToken cancellationToken = default)
    {
        var categoryLabel = category.ToString();
        await _store.AddApprovalDecisionAsync(new ApprovalDecisionAuditInput(requestId,
                toolName,
                categoryLabel,
                ApprovalDecisions.Approve,
                $"mcp-agentic:{keyPrefix}",
                LatencyMs: 0),
            cancellationToken).ConfigureAwait(false);
        NodeMetrics.ToolApprovalDecisionsTotal.Add(1,
            new KeyValuePair<string, object?>("category", categoryLabel),
            new KeyValuePair<string, object?>("decision", ApprovalDecisions.Approve));
    }
}
