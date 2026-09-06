namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A second human act on a pause that is already answered — a NEW operation id on a decided row, or an id that
///     already decided a DIFFERENT pause of the same run. Neither is the idempotent replay a repeated id is.
///     <para>
///         It carries the answer that STANDS, and that is the whole reason it is its own type rather than another
///         <see cref="GraphWorkflowRunConflictException" />: the second person to click needs to be told what was
///         decided, not only that their click failed. <c>ConflictExceptionHandler</c> puts
///         <see cref="StandingDecision" /> on the 409 body under the member Dev Workflows' equivalent already
///         populates.
///     </para>
/// </summary>
public sealed class GraphWorkflowGateAlreadyDecidedException(string message, GraphWorkflowDecisionKind standingDecision) : InvalidOperationException(message)
{
    public GraphWorkflowDecisionKind StandingDecision { get; } = standingDecision;
}
