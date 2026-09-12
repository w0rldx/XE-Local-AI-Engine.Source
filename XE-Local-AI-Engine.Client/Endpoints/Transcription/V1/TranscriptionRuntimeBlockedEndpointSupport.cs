namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Builds the 409 body every blocked transcription-runtime mutation returns. A deliberate typed DTO rather than
///     the shared problem-details envelope, because the SPA branches on the reason code and renders the activity
///     snapshot; keeping it here means the endpoints that produce it cannot drift apart and the busy reason exists once.
/// </summary>
internal static class TranscriptionRuntimeBlockedEndpointSupport
{
    /// <summary>The reason code the SPA matches to show "wait for the running work" rather than an error.</summary>
    private const string RuntimeBusyReason = "runtime-busy";

    internal static IResult Blocked(string reason, string message, WhisperRuntimeActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return Results.Conflict(new TranscriptionRuntimeBlockedResponse
        {
            Reason = reason,
            Message = message,
            Activity = activity.ToResponse()
        });
    }

    internal static IResult RuntimeBusy(string message, WhisperRuntimeActivitySnapshot activity) =>
        Blocked(RuntimeBusyReason, message, activity);
}
