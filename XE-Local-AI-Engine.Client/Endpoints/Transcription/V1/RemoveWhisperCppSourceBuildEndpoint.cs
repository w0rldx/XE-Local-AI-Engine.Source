namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Deletes the adopted managed runtime and its record. This is the in-app recovery from a fail-closed tombstone:
///     without it, a record proven unusable can only be cleared by hand. Refuses with <c>409 runtime-busy</c> while
///     anything holds the runtime. Operator-gated.
/// </summary>
public sealed class RemoveWhisperCppSourceBuildEndpoint(WhisperRuntimeOrchestrationService whisperRuntime)
    : Endpoint<TranscriptionRuntimeActionRequest, WhisperCppSourceBuildStatusResponse>
{
    private readonly WhisperRuntimeOrchestrationService _whisperRuntime =
        whisperRuntime ?? throw new ArgumentNullException(nameof(whisperRuntime));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.RuntimeSourceBuildRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<TranscriptionRuntimeActionRequest>("application/json")
                               .Produces<WhisperCppSourceBuildStatusResponse>(StatusCodes.Status200OK)
                               .Produces<TranscriptionRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(TranscriptionRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await _whisperRuntime.RemoveAsync(ct).ConfigureAwait(false);
            if (result.Outcome == WhisperCppSourceBuildRemoveOutcome.RuntimeBusy)
            {
                await Send.ResultAsync(TranscriptionRuntimeBlockedEndpointSupport.RuntimeBusy(
                              "Wait for active transcriptions and transcription-runtime processes to finish before removing the managed runtime.",
                              result.Activity ?? _whisperRuntime.GetActivitySnapshot()))
                          .ConfigureAwait(false);
                return;
            }

            // Removed and NotInstalled are both successes: the caller asked for "no managed runtime", and either way
            // that is now true.
            if (result.Outcome is not (WhisperCppSourceBuildRemoveOutcome.Removed or WhisperCppSourceBuildRemoveOutcome.NotInstalled))
            {
                throw new InvalidOperationException($"Unknown whisper.cpp source-build remove outcome: {result.Outcome}.");
            }

            await Send.OkAsync(_whisperRuntime.GetStatus().ToResponse(), ct).ConfigureAwait(false);
        }
        catch (WhisperRuntimeException exception)
        {
            // A directory that will not delete is the recovery path failing, and the operator needs the reason in the
            // same typed envelope Start uses rather than a 500 from the global handler. The message is contractually
            // sanitized, so it is safe to surface.
            await Send.ResultAsync(TranscriptionRuntimeBlockedEndpointSupport.Blocked("source-build-error",
                          exception.Message,
                          _whisperRuntime.GetActivitySnapshot()))
                      .ConfigureAwait(false);
        }
    }
}
