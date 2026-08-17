namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Builds the 409 <see cref="ImageRuntimeBlockedResponse" /> every image-runtime mutation returns when the runtime
///     is unavailable. A deliberate typed DTO rather than the shared <c>ConflictProblemDetails</c> envelope, because
///     the SPA branches on <c>reason</c> and renders the <c>activity</c> snapshot; keeping the envelope here means the
///     four endpoints that produce it cannot drift apart, and the busy reason code exists once.
/// </summary>
internal static class ImageRuntimeBlockedEndpointSupport
{
    /// <summary>The reason code the SPA matches to show "wait for running image work" rather than a build error.</summary>
    private const string RuntimeBusyReason = "runtime-busy";

    internal static IResult Blocked(string reason, string message, ImageRuntimeActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return Results.Conflict(new ImageRuntimeBlockedResponse
        {
            Reason = reason,
            Message = message,
            Activity = activity.ToResponse()
        });
    }

    internal static IResult RuntimeBusy(string message, ImageRuntimeActivitySnapshot activity) =>
        Blocked(RuntimeBusyReason, message, activity);
}
