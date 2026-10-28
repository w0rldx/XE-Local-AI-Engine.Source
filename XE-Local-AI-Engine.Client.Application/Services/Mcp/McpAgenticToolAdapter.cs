namespace XE_Local_AI_Engine.Client.Services.Mcp;

using System.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class McpAgenticToolAdapter : IMcpAgenticToolAdapter
{
    private readonly IUnattendedApprovalAuditRecorder _auditRecorder;
    private readonly ILogger<McpAgenticToolAdapter> _logger;

    public McpAgenticToolAdapter(IUnattendedApprovalAuditRecorder auditRecorder,
        ILogger<McpAgenticToolAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(auditRecorder);
        ArgumentNullException.ThrowIfNull(logger);
        _auditRecorder = auditRecorder;
        _logger = logger;
    }

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

    private sealed class AutoApprovedFunction : DelegatingAIFunction
    {
        private readonly ToolCategory _category;
        private readonly string _keyPrefix;
        private readonly Guid _requestId;
        private readonly IUnattendedApprovalAuditRecorder _auditRecorder;
        private readonly ILogger _logger;

        public AutoApprovedFunction(ApprovalRequiredAIFunction approvalRequired,
            ToolCategory category,
            string keyPrefix,
            Guid requestId,
            IUnattendedApprovalAuditRecorder auditRecorder,
            ILogger logger) : base(approvalRequired)
        {
            _category = category;
            _keyPrefix = keyPrefix;
            _requestId = requestId;
            _auditRecorder = auditRecorder;
            _logger = logger;
        }

        // FunctionInvokingChatClient detects approval with GetService<ApprovalRequiredAIFunction>(), which DelegatingAIFunction forwards to the
        // wrapped function: without this override the auto-approved call would still become an approval request nobody answers.
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(ApprovalRequiredAIFunction) ? null : base.GetService(serviceType, serviceKey);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var auditSucceeded = false;
            try
            {
                await _auditRecorder.RecordAsync(_requestId, Name, _category, IUnattendedApprovalAuditRecorder.McpAgentic(_keyPrefix), cancellationToken);
                auditSucceeded = true;
                return await InnerFunction.InvokeAsync(arguments, cancellationToken);
            }
            finally
            {
                var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _logger.LogInformation(
                    "Agentic MCP tool invocation {Decision}: Tool={ToolName} Category={Category} KeyPrefix={KeyPrefix} RequestId={RequestId} DurationMs={DurationMs} AuditSucceeded={AuditSucceeded}",
                    ApprovalDecisions.Approve,
                    Name,
                    _category,
                    _keyPrefix,
                    _requestId,
                    durationMs,
                    auditSucceeded);
            }
        }
    }
}
