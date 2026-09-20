namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

/// <summary>
///     Builds the 409 <see cref="TrainingRuntimeBlockedResponse" /> both training-runtime mutations return when the
///     runtime cannot be changed right now.
/// </summary>
/// <remarks>
///     A deliberate typed DTO rather than the shared <c>ConflictProblemDetails</c> envelope, because most of these
///     refusals are a value the service returns — an install outcome, a prerequisite checklist — and a body no
///     exception ever reaches cannot be an exception handler's to write (ADR 0009). Keeping the envelope here means
///     the install and remove routes cannot drift apart, and the reason code they share exists once.
/// </remarks>
internal static class TrainingRuntimeBlockedEndpointSupport
{
    /// <summary>The reason code both routes report while an install holds the runtime; each states its own next step.</summary>
    internal const string AlreadyInstallingReason = "already-installing";

    internal static IResult Blocked(string reason, string message, TrainingRuntimePrerequisitesResponse? prerequisites = null) =>
        Results.Conflict(new TrainingRuntimeBlockedResponse
        {
            Reason = reason,
            Message = message,
            Prerequisites = prerequisites
        });
}
