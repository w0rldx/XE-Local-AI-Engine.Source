namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Operator-initiated in-app CUDA build (POST model-fit/llamacpp/cuda-build). Hard-gated SERVER-SIDE (not UI-only):
///     Linux · all prerequisites met · sufficient free disk · no running llama-server processes (eject-first) ·
///     single-flight. Each gate that fails returns a sanitized 409 with a stable reason code. On success it starts the
///     background build and returns the initial status; live progress streams over the CUDA build hub.
/// </summary>
public sealed class StartCudaBuildEndpoint(
    ILlamaCppSourceBuildService sourceBuildService,
    ICudaBuildService buildService) : EndpointWithoutRequest<StartCudaBuildResponse>
{
    private readonly ICudaBuildService _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));

    private readonly ILlamaCppSourceBuildService _sourceBuildService =
        sourceBuildService ?? throw new ArgumentNullException(nameof(sourceBuildService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CudaBuild);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Linux gate (server-side, not UI-only).
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "The in-app CUDA build is available on Linux only.").ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _sourceBuildService.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cuda,
                LlamaCppSourceSelection.Official), ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case LlamaCppSourceBuildStartOutcome.AlreadyRunning:
                    await BlockAsync("already-building", "A CUDA build is already in progress.").ConfigureAwait(false);
                    return;
                case LlamaCppSourceBuildStartOutcome.InsufficientDisk:
                    await BlockAsync("disk", "There is not enough free disk space to build the CUDA runtime.").ConfigureAwait(false);
                    return;
                case LlamaCppSourceBuildStartOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                            "One or more build prerequisites are missing; resolve the checklist before building.")
                        .ConfigureAwait(false);
                    return;
                case LlamaCppSourceBuildStartOutcome.ProcessesRunning:
                    await Send.ResultAsync(Results.Conflict(new CudaBuildBlockedResponse
                    {
                        Reason = "processes-running",
                        Message = "Stop or eject all running llama.cpp models before building the runtime.",
                        RunningProcessCount = result.RunningProcessCount
                    })).ConfigureAwait(false);
                    return;
                case LlamaCppSourceBuildStartOutcome.RuntimeBusy:
                    await BlockAsync("runtime-busy",
                            "Wait for the active llama.cpp source build or runtime change to finish before starting another build.")
                        .ConfigureAwait(false);
                    return;
                case LlamaCppSourceBuildStartOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown source-build start outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartCudaBuildResponse
                {
                    Started = true,
                    Status = _buildService.GetStatus().ToResponse()
                },
                ct).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException exception)
        {
            // A race lost the prerequisite/disk re-check inside the service — surface the sanitized reason as a 409.
            await BlockAsync("prerequisites", exception.Message).ConfigureAwait(false);
        }
    }

    private Task BlockAsync(string reason, string message)
    {
        return Send.ResultAsync(Results.Conflict(new CudaBuildBlockedResponse
        {
            Reason = reason,
            Message = message
        }));
    }
}
