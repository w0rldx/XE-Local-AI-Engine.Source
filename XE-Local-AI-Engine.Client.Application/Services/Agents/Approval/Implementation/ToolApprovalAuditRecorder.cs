namespace XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IToolApprovalAuditRecorder" />: increments the content-free approval-decision counter and
///     appends a metadata-only audit row through <see cref="IAgentExecutionLogStore" />.
/// </summary>
/// <remarks>
///     Singleton, because the approval runner is one, while the log store is scoped — so each decision opens its own
///     short-lived scope, which is negligible at human pace. The whole write is wrapped defensively: an audit failure
///     is swallowed with a content-free warning so it can never break or delay the operator's decision.
/// </remarks>
internal sealed class ToolApprovalAuditRecorder : IToolApprovalAuditRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ToolApprovalAuditRecorder> _logger;

    public ToolApprovalAuditRecorder(IServiceScopeFactory scopeFactory, ILogger<ToolApprovalAuditRecorder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAsync(Guid? invocationId,
        string toolName,
        ToolCategory category,
        string decision,
        string source,
        long latencyMs,
        CancellationToken cancellationToken = default)
    {
        var categoryLabel = category.ToString();

        try
        {
            // Content-free counter (category + decision only). Incremented first so a decision is still counted even if
            // the durable row write below fails — the two signals are independent.
            NodeMetrics.ToolApprovalDecisionsTotal.Add(1,
                new KeyValuePair<string, object?>("category", categoryLabel),
                new KeyValuePair<string, object?>("decision", decision));

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
            await store.AddApprovalDecisionAsync(new ApprovalDecisionAuditInput
                {
                    InvocationId = invocationId,
                    ToolName = toolName ?? string.Empty,
                    Category = categoryLabel,
                    Decision = decision,
                    Source = source,
                    LatencyMs = latencyMs
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Defensive by contract: the audit is best-effort and must NEVER break or delay the approval round-trip,
            // so every failure is swallowed with a content-free warning carrying the invocation id and nothing else.
            _logger.LogWarning(exception,
                "Failed to record tool-approval decision audit for invocation {InvocationId}; the approval decision is unaffected.",
                invocationId);
        }
    }
}
