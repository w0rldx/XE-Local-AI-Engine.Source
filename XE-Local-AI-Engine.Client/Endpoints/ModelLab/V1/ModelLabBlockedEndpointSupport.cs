namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Builds the 409 <see cref="ModelLabStartBlockedResponse" /> every experiment route answers with when its refusal is a returned outcome (ADR 0009).</summary>
internal static class ModelLabBlockedEndpointSupport
{
    internal static IResult Blocked(ModelLabStartRefusal reason, string message) =>
        TypedResults.Conflict(new ModelLabStartBlockedResponse
        {
            Reason = reason,
            Message = message
        });

    internal static IResult StillRunning() =>
        Blocked(ModelLabStartRefusal.Busy, "The experiment is still running. Cancel it first.");
}
