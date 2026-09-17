namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Read-only in-app CUDA build status (GET model-fit/llamacpp/cuda-build/status): the current phase, whether a build
///     is running/terminal, and the last N streamed log lines (the one-shot hydrate on mount; live progress streams over
///     the CUDA build hub).
/// </summary>
public sealed class GetCudaBuildStatusEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    : EndpointWithoutRequest<CudaBuildStatusResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.CudaBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_runtime.GetCudaBuildStatus().ToResponse(), ct);
    }
}
