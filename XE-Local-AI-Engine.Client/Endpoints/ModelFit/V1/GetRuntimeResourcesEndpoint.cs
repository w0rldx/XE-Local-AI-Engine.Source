namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     FastEndpoints handler for live memory usage (GET model-fit/resources): whole-machine RAM plus one VRAM entry per
///     measured GPU, for the top-bar gauge.
/// </summary>
/// <remarks>
///     Served from <see cref="ILiveMemorySampler" />, never the hardware profiler's cache. Unknown VRAM is an empty
///     <c>gpus</c> list rather than an error, so the gauge can poll on any host.
/// </remarks>
public sealed class GetRuntimeResourcesEndpoint : EndpointWithoutRequest<RuntimeResourcesResponse>
{
    private readonly ILiveMemorySampler _sampler;

    public GetRuntimeResourcesEndpoint(ILiveMemorySampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        _sampler = sampler;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Resources);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sample = await _sampler.SampleAsync(ct);
        await Send.OkAsync(sample.ToResponse(), ct);
    }
}
