namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

/// <summary>
///     Builds the 409 <see cref="BaseArtifactBlockedResponse" /> every base-checkpoint mutation returns when the
///     request is refused.
/// </summary>
/// <remarks>
///     A deliberate typed DTO rather than the shared <c>ConflictProblemDetails</c> envelope, so a rejected selection,
///     a download that is not running and a checkpoint still downloading each carry their own stable reason code
///     instead of one generic conflict type (ADR 0009). Keeping the envelope here means the three routes that produce
///     it cannot drift apart.
/// </remarks>
internal static class BaseArtifactBlockedEndpointSupport
{
    internal static IResult Blocked(string reason, string message) =>
        Results.Conflict(new BaseArtifactBlockedResponse
        {
            Reason = reason,
            Message = message
        });
}
