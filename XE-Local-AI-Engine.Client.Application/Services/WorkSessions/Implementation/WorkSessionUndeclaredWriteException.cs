namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

/// <summary>
///     A turn refused before it was sent because its own tool offer carried a write/execute tool the development-workflow
///     node driving it never declared (<c>GRAPH-C4-2</c>).
/// </summary>
/// <remarks>
///     Thrown out of the send path rather than streamed as a terminal, so it cannot be mistaken for a provider
///     failure: the supervisor catches it, records the gate that stopped the step, and settles the session with this
///     message, which the owning run then blocks its node run with under the <c>Policy</c> failure class.
/// </remarks>
internal sealed class WorkSessionUndeclaredWriteException : InvalidOperationException
{
    public WorkSessionUndeclaredWriteException(string message) : base(message)
    {
    }
}
