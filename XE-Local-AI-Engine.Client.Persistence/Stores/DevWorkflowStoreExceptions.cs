namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class DevWorkflowConcurrencyException : InvalidOperationException
{
    public DevWorkflowConcurrencyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public class DevWorkflowInvalidTransitionException : InvalidOperationException
{
    public DevWorkflowInvalidTransitionException(string message) : base(message)
    {
    }
}

/// <summary>
///     The one invalid transition that is an ACCOUNTING refusal rather than an illegal move: the run has no re-attempt
///     left to spend on this act. Its own type because the automatic retry path converts it to a Blocked node run with
///     the <c>BudgetExhausted</c> class, and catching the base type there would relabel every illegal move — a row
///     settled terminal under the tick, say — as budget exhaustion on a run with its whole budget intact.
///     <para>
///         Derived rather than separate so every existing <c>catch</c> of the base type (endpoint mapping, validation
///         translation) goes on behaving exactly as it does today. Public rather than internal because the retry
///         policy that catches it lives in the application assembly.
///     </para>
/// </summary>
public sealed class DevWorkflowRetryBudgetExceededException : DevWorkflowInvalidTransitionException
{
    public DevWorkflowRetryBudgetExceededException(string message) : base(message)
    {
    }
}

public sealed class DevWorkflowNotFoundException : InvalidOperationException
{
    public DevWorkflowNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
///     A work item that already has a live run was asked for a second one, or asked to be deleted. Its own conflict
///     type rather than an invalid transition, because the answer is different: wait for the run, or cancel it.
/// </summary>
public sealed class DevWorkflowRunInFlightException : InvalidOperationException
{
    public DevWorkflowRunInFlightException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>
///     A second human act on a gate that is already answered — a NEW operation id arriving at a decided node-run,
///     which is not the idempotent replay a repeated one is.
///     <para>
///         <see cref="StandingDecision" /> travels with it so the API can tell the operator WHAT was decided instead
///         of only that their click failed.
///     </para>
/// </summary>
public sealed class DevWorkflowGateAlreadyDecidedException : InvalidOperationException
{
    public DevWorkflowGateAlreadyDecidedException(string message, DevWorkflowDecisionKind standingDecision) : base(message)
    {
        StandingDecision = standingDecision;
    }

    public DevWorkflowDecisionKind StandingDecision { get; }
}
