namespace XE_Local_AI_Engine.Client.Services.Mcp;

using System.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class McpAgenticToolAdapter(
    IMcpAgenticApprovalAuditRecorder auditRecorder,
    ILogger<McpAgenticToolAdapter> logger) : IMcpAgenticToolAdapter
{
    private readonly IMcpAgenticApprovalAuditRecorder _auditRecorder =
        auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));

    private readonly ILogger<McpAgenticToolAdapter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public AIFunction Adapt(ApprovalRequiredAIFunction approvalRequired,
        ToolCategory category,
        McpInboundExecutionContext context,
        Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(approvalRequired);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsAgentic || !McpInboundExecutionContext.IsBoundedPrefix(context.KeyPrefix) || requestId == Guid.Empty)
        {
            throw new InvalidOperationException("Agentic MCP tool adaptation requires captured agentic authority and a request identity.");
        }

        return new AutoApprovedFunction(approvalRequired,
            category,
            context.KeyPrefix!,
            requestId,
            _auditRecorder,
            _logger);
    }

    private sealed class AutoApprovedFunction(
        ApprovalRequiredAIFunction approvalRequired,
        ToolCategory category,
        string keyPrefix,
        Guid requestId,
        IMcpAgenticApprovalAuditRecorder auditRecorder,
        ILogger logger) : DelegatingAIFunction(approvalRequired)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var auditSucceeded = false;
            try
            {
                await auditRecorder.RecordAsync(requestId, Name, category, keyPrefix, cancellationToken).ConfigureAwait(false);
                auditSucceeded = true;
                return await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                logger.LogInformation(
                    "Agentic MCP tool invocation {Decision}: Tool={ToolName} Category={Category} KeyPrefix={KeyPrefix} RequestId={RequestId} DurationMs={DurationMs} AuditSucceeded={AuditSucceeded}",
                    ApprovalDecisions.Approve,
                    Name,
                    category,
                    keyPrefix,
                    requestId,
                    durationMs,
                    auditSucceeded);
            }
        }
    }
}
