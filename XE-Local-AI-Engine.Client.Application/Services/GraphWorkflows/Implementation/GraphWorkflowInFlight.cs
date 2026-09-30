namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Runtime.CompilerServices;

/// <summary>
///     One node run's work in flight: the task, the token that stops it, and the two facts the dispatcher needs about
///     it without touching the task itself.
/// </summary>
/// <remarks>
///     <see cref="Attempt" /> is what makes an answer belong to a try: a retry lands the row on a new attempt, and a
///     pass belonging to the one before is not an answer about the one the row is on now. <see cref="InvocationId" />
///     is minted before the work starts, because the stop path has to have something to hand to whatever knows how to
///     unwind it. <see cref="LeaseAcquired" /> is a <see cref="StrongBox{T}" /> rather than a <see cref="bool" /> so
///     the task body can flip it and the poll can see it: the row reads <c>Queued</c> until the work holds its slot.
/// </remarks>
internal sealed class GraphWorkflowInFlight<TResult>
{
    public required CancellationTokenSource Cancellation { get; init; }

    public required Task<TResult> Work { get; init; }

    public required int Attempt { get; init; }

    public required Guid InvocationId { get; init; }

    public required StrongBox<bool> LeaseAcquired { get; init; }
}
