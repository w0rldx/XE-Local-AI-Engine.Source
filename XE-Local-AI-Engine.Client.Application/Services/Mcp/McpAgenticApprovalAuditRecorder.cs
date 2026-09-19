namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Persists agentic auto-approval before execution; unlike the human recorder, failures propagate.</summary>
internal sealed class McpAgenticApprovalAuditRecorder : IMcpAgenticApprovalAuditRecorder
{
    private readonly IAgentExecutionLogStore _store;

    public McpAgenticApprovalAuditRecorder(IAgentExecutionLogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task RecordAsync(Guid requestId,
        string toolName,
        ToolCategory category,
        string keyPrefix,
        CancellationToken cancellationToken = default)
    {
        var categoryLabel = category.ToString();
        await _store.AddApprovalDecisionAsync(new ApprovalDecisionAuditInput
        {
            InvocationId = requestId,
            ToolName = toolName,
            Category = categoryLabel,
            Decision = ApprovalDecisions.Approve,
            Source = $"mcp-agentic:{keyPrefix}",
            LatencyMs = 0
        },
            cancellationToken);
        NodeMetrics.ToolApprovalDecisionsTotal.Add(1,
            new KeyValuePair<string, object?>("category", categoryLabel),
            new KeyValuePair<string, object?>("decision", ApprovalDecisions.Approve));
    }
}
