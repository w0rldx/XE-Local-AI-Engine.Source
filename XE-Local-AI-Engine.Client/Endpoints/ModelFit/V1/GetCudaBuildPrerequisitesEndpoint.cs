namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Read-only in-app CUDA build prerequisite checklist (GET model-fit/llamacpp/cuda-build/prerequisites). Reports, item
///     by item, what toolchain the source build needs and whether it is present, plus the overall <c>canBuild</c> gate
///     (true only on Linux when every item is satisfied). Installs nothing. Works on any OS — a non-Linux host reports a
///     single unsatisfied OS item with <c>canBuild=false</c>.
/// </summary>
public sealed class GetCudaBuildPrerequisitesEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    : EndpointWithoutRequest<CudaBuildPrerequisitesResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.CudaBuildPrerequisites);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var report = await _runtime.ProbeCudaBuildPrerequisitesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(report.ToResponse(), ct).ConfigureAwait(false);
    }
}
