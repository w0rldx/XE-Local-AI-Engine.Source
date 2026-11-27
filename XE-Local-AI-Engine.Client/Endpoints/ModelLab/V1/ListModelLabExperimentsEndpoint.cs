namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class ListModelLabExperimentsEndpoint : EndpointWithoutRequest<ModelLabExperimentListResponse>
{
    private readonly ModelLabExperimentService _experiments;
    private readonly ModelLabExperimentReader _reader;

    public ListModelLabExperimentsEndpoint(ModelLabExperimentService experiments, ModelLabExperimentReader reader)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        ArgumentNullException.ThrowIfNull(reader);
        _experiments = experiments;
        _reader = reader;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.Experiments);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ModelLabExperimentListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _reader.ListAsync(ct);
        var supported = _experiments.IsPlatformSupported;
        await Send.OkAsync(new ModelLabExperimentListResponse
        {
            PlatformSupported = supported,
            UnsupportedMessage = supported ? null : ModelLabExperimentService.UnsupportedPlatformMessage,
            ActiveExperimentId = _experiments.ActiveExperimentId,
            MaxRetainedExperiments = ModelLabExperimentService.MaxRetainedExperiments,
            MaxPinnedExperiments = ModelLabExperimentService.MaxPinnedExperiments,
            Experiments = [.. records.Select(static record => ModelLabEndpointMapper.ToResponse(record, detail: false))]
        }, ct);
    }
}
