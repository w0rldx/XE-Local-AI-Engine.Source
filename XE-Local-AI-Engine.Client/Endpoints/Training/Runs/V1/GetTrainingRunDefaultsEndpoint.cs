namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     The wizard's computed starting point: options sized to this box, the VRAM estimate behind them, and the exact
///     licensing text the operator has to confirm. Read-only — it creates nothing.
/// </summary>
public sealed class GetTrainingRunDefaultsEndpoint : Endpoint<TrainingRunDefaultsRequest, TrainingRunDefaultsResponse>
{
    private readonly ITrainingOptionDefaultsCalculator _defaults;
    private readonly ILicenseGateService _licenseGate;
    private readonly IInstalledBaseModelLinker _linker;

    public GetTrainingRunDefaultsEndpoint(ITrainingOptionDefaultsCalculator defaults, ILicenseGateService licenseGate, IInstalledBaseModelLinker linker)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(licenseGate);
        ArgumentNullException.ThrowIfNull(linker);
        _defaults = defaults;
        _licenseGate = licenseGate;
        _linker = linker;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunDefaults);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TrainingRunDefaultsResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(TrainingRunDefaultsRequest req, CancellationToken ct)
    {
        var computed = await _defaults.ComputeAsync(req.BaseArtifactId, ct);
        var license = await _licenseGate.GetAsync(req.BaseArtifactId, ct);
        var suggestions = license is null
            ? []
            : await _linker.SuggestAsync(license.RepoId, ct);
        await Send.OkAsync(computed.ToResponse(license, suggestions), ct);
    }
}
