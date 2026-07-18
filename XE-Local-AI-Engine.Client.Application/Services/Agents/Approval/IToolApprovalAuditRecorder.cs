namespace XE_Local_AI_Engine.Client.Services.Agents.Approval;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Records a single RESOLVED tool-approval decision (approve / deny / timeout) as a metadata-only audit row plus a
///     content-free metric increment (OPP-03). The implementation is fire-and-forget-safe: a failed write is swallowed so
///     it can NEVER break or delay the approval round-trip. It never carries tool arguments or message content — only the
///     tool name, its risk category, the decision, the source, and the request→decision latency.
/// </summary>
public interface IToolApprovalAuditRecorder
{
    /// <summary>
    ///     Records one approval decision. <paramref name="decision" /> is one of
    ///     <see cref="XE_Local_AI_Engine.Client.Persistence.Stores.ApprovalDecisions" /> and <paramref name="source" /> one
    ///     of <see cref="XE_Local_AI_Engine.Client.Persistence.Stores.ApprovalDecisionSources" />. Never throws.
    /// </summary>
    Task RecordAsync(Guid? invocationId,
        string toolName,
        ToolCategory category,
        string decision,
        string source,
        long latencyMs,
        CancellationToken cancellationToken = default);
}
