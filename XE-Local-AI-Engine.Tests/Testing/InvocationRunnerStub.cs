namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>An <see cref="IInvocationRunner" /> double that only runs turns: every other member is an idle no-op.</summary>
internal abstract class InvocationRunnerStub : IInvocationRunner
{
    public int ActiveInvocationCount => 0;

    public abstract Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default);

    public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public void Cancel(Guid invocationId)
    {
    }

    public void CancelDetached(Guid invocationId)
    {
    }

    public void CancelAll()
    {
    }

    public virtual void CleanupStaleToolCalls(TimeSpan maxAge)
    {
    }

    public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
    {
    }

    public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
    {
    }
}
