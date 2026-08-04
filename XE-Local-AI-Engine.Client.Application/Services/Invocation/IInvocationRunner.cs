namespace XE_Local_AI_Engine.Client.Services.Invocation;

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

    void CancelAll();

    void CleanupStaleToolCalls(TimeSpan maxAge);

    void ResolveApprovalResult(ApprovalResolvedEvent evt);

    /// <summary>
    ///     Releases a turn parked on an <c>ask_user</c> question by handing it the operator's answers. Mirrors
    ///     <see cref="ResolveApprovalResult" />: keyed on the opaque question request id, and a no-op when no question
    ///     is pending for that id, so a duplicate or stale answer can never fault the turn.
    /// </summary>
    void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt);

    void ResolveToolCallResult(ToolCallResultEvent evt);
}
