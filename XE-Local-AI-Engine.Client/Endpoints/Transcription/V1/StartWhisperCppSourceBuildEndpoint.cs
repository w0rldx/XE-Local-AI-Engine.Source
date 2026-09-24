namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Starts the managed Linux CUDA source build and returns as soon as it is running; progress is polled from the
///     status route. Operator-gated.
/// </summary>
/// <remarks>
///     Every refusal is a 409 carrying a reason code and the activity snapshot, so the operator is told what to wait
///     for or what to install.
/// </remarks>
public sealed class StartWhisperCppSourceBuildEndpoint : Endpoint<StartWhisperCppSourceBuildRequest, StartWhisperCppSourceBuildResponse>
{
    private readonly WhisperRuntimeOrchestrationService _whisperRuntime;

    public StartWhisperCppSourceBuildEndpoint(WhisperRuntimeOrchestrationService whisperRuntime)
    {
        ArgumentNullException.ThrowIfNull(whisperRuntime);
        _whisperRuntime = whisperRuntime;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.RuntimeSourceBuild);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartWhisperCppSourceBuildResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<TranscriptionRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartWhisperCppSourceBuildRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Answered here rather than by letting the service throw, so a Windows or macOS node gets the same typed
        // conflict shape as every other refusal instead of an exception mapped to a 500.
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "In-app source builds are available on Linux only.");
            return;
        }

        try
        {
            var result = await _whisperRuntime.StartAsync(request.ToContract(), ct);
            switch (result.Outcome)
            {
                case WhisperCppSourceBuildStartOutcome.AlreadyRunning:
                    await BlockAsync("already-building", "A whisper.cpp source build is already in progress.", result.Activity);
                    return;
                case WhisperCppSourceBuildStartOutcome.InsufficientDisk:
                    await BlockAsync("prerequisites",
                        "There is not enough free disk space to build the transcription runtime.",
                        result.Activity);
                    return;
                case WhisperCppSourceBuildStartOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                        "One or more build prerequisites are missing; resolve the checklist before building.",
                        result.Activity);
                    return;
                case WhisperCppSourceBuildStartOutcome.RuntimeBusy:
                    await BlockAsync("runtime-busy",
                        "Wait for active transcriptions and transcription-runtime processes to finish before starting the build.",
                        result.Activity);
                    return;
                case WhisperCppSourceBuildStartOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown whisper.cpp source-build start outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartWhisperCppSourceBuildResponse
            {
                Started = true,
                Status = _whisperRuntime.GetStatus().ToResponse()
            }, ct);
        }
        catch (WhisperRuntimeException exception)
        {
            // The message is contractually sanitized, so it is safe to surface and is the one that says what is wrong.
            await BlockAsync("source-build-error", exception.Message);
        }
    }

    private Task BlockAsync(string reason, string message, WhisperRuntimeActivitySnapshot? activity = null) =>
        Send.ResultAsync(TranscriptionRuntimeBlockedEndpointSupport.Blocked(reason, message, activity ?? _whisperRuntime.GetActivitySnapshot()));
}
