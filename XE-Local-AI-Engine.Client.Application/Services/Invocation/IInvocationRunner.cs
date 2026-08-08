namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Abstraction for invocation runner behavior.
/// </summary>
public interface IInvocationRunner
{
    int ActiveInvocationCount { get; }

    Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default);

    Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default);

    void Cancel(Guid invocationId);

    /// <summary>
    ///     Cancels a run whose last client disconnected and stayed away past the disconnect grace
    ///     (<c>DetachedInvocationReaper</c>). Identical to <see cref="Cancel" /> except for how the turn is attributed:
    ///     an abandoned turn, not an operator stop. Both terminalize the row <c>Cancelled</c>.
    /// </summary>
    void CancelDetached(Guid invocationId);

    void CancelAll();

    void CleanupStaleToolCalls(TimeSpan maxAge);

    /// <summary>
    ///     Releases a turn parked on a tool-approval request with the operator's decision. <paramref name="scope" /> is
    ///     how long an APPROVE lasts and defaults to <see cref="ApprovalScope.Once" />, so the platform-hub path — which
    ///     has no notion of session scope, and whose wire event deliberately does not carry one — keeps its exact
    ///     previous behaviour. A deny is never remembered whatever the scope.
    /// </summary>
    void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once);

    /// <summary>
    ///     Releases a turn parked on an <c>ask_user</c> question by handing it the operator's answers. Mirrors
    ///     <see cref="ResolveApprovalResult" />: keyed on the opaque question request id, and a no-op when no question
    ///     is pending for that id, so a duplicate or stale answer can never fault the turn.
    /// </summary>
    void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt);

    void ResolveToolCallResult(ToolCallResultEvent evt);
}
