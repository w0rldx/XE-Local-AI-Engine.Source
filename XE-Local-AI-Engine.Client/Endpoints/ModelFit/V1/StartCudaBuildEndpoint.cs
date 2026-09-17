namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Operator-initiated in-app CUDA build (POST model-fit/llamacpp/cuda-build). Hard-gated SERVER-SIDE (not UI-only):
///     Linux · all prerequisites met · sufficient free disk · no running llama-server processes (eject-first) ·
///     single-flight. Each gate that fails returns a sanitized 409 with a stable reason code. On success it starts the
///     background build and returns the initial status; live progress streams over the CUDA build hub.
/// </summary>
public sealed class StartCudaBuildEndpoint(
    LlamaCppRuntimeOrchestrationService runtime,
    INodeRuntimeSettings nodeRuntimeSettings) : EndpointWithoutRequest<StartCudaBuildResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CudaBuild);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartCudaBuildResponse>(StatusCodes.Status200OK)
                               .Produces<CudaBuildBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Linux gate (server-side, not UI-only).
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "The in-app CUDA build is available on Linux only.");
            return;
        }

        if (await LlamaCppPrebuiltRuntimeMutationGuard
                  .IsKeepModelWarmEnabledAsync(nodeRuntimeSettings, ct))
        {
            await BlockAsync("keep-model-warm-enabled", LlamaCppPrebuiltRuntimeMutationGuard.KeepModelWarmBlockedMessage);
            return;
        }

        try
        {
            var result = await _runtime.StartSourceBuildAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cuda,
                LlamaCppSourceSelection.Official), ct);
            var blocked = LlamaCppSourceBuildStartEndpointSupport.MapBlocked(result.Outcome,
                LlamaCppSourceBuildStartEndpointSupport.CudaBuildKind);
            if (blocked is not null)
            {
                var (reason, message) = blocked.Value;

                // Only the process gate reports a count; every other rejection leaves it null as before.
                await Send.ResultAsync(Results.Conflict(new CudaBuildBlockedResponse
                {
                    Reason = reason,
                    Message = message,
                    RunningProcessCount = result.Outcome == LlamaCppSourceBuildStartOutcome.ProcessesRunning
                        ? result.RunningProcessCount
                        : null
                }));
                return;
            }

            await Send.OkAsync(new StartCudaBuildResponse
                {
                    Started = true,
                    Status = _runtime.GetCudaBuildStatus().ToResponse()
                },
                ct);
        }
        catch (LlamaRuntimeException exception)
        {
            // A race lost the prerequisite/disk re-check inside the service — surface the sanitized reason as a 409.
            await BlockAsync("prerequisites", exception.Message);
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
