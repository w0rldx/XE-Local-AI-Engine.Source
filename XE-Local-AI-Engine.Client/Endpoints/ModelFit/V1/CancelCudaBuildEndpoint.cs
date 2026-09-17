namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Cancels an in-flight in-app CUDA build (POST model-fit/llamacpp/cuda-build/cancel). Idempotent: a no-op when no
///     build is running. Returns the current status (the build tears down its process group + cleans partial trees
///     asynchronously, transitioning to <c>Cancelled</c>).
/// </summary>
public sealed class CancelCudaBuildEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    : EndpointWithoutRequest<CudaBuildStatusResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CudaBuildCancel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Cancel() returns false when nothing is in flight — its own "already cancelling / nothing running" guard. [secLOW-2]
        _runtime.CancelCudaBuild();
        await Send.OkAsync(_runtime.GetCudaBuildStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
