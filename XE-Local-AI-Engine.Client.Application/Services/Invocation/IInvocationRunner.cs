namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Abstraction for invocation runner behavior.
/// </summary>
public interface IInvocationRunner
{
    int ActiveInvocationCount { get; }

    Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default);

    Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    void Cancel(Guid invocationId);

    /// <summary>
    ///     Cancels a run whose last client disconnected and stayed away past the disconnect grace
    ///     (<c>DetachedInvocationReaper</c>).
    /// </summary>
    /// <remarks>
    ///     Identical to <see cref="Cancel" /> except for how the turn is attributed: an abandoned turn, not an operator
    ///     stop. Both terminalize the row <c>Cancelled</c>.
    /// </remarks>
    void CancelDetached(Guid invocationId);

    void CancelAll();

    void CleanupStaleToolCalls(TimeSpan maxAge);

    /// <summary>
    ///     Releases a turn parked on a tool-approval request with the operator's decision. <paramref name="scope" /> is
    ///     how long an APPROVE lasts and defaults to <see cref="ApprovalScope.Once" />. A deny is never remembered
    ///     whatever the scope.
    /// </summary>
    void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once);

    /// <summary>Releases a turn parked on an <c>ask_user</c> question by handing it the operator's answers.</summary>
    /// <remarks>
    ///     Mirrors <see cref="ResolveApprovalResult" />: keyed on the opaque question request id, and a no-op when no
    ///     question is pending for that id, so a duplicate or stale answer can never fault the turn.
    /// </remarks>
    void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt);
}
