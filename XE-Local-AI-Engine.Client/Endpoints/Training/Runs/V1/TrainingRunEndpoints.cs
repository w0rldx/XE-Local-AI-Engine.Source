namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     Starts a run. The license confirmation is enforced here AND in the store's create transaction — the endpoint so
///     the operator gets a 400 rather than a 500, the store so no other caller can bypass it.
/// </summary>
public sealed class CreateTrainingRunEndpoint(ITrainingRunService runs) : Endpoint<CreateTrainingRunRequest, TrainingRunResponse>
{
    private readonly ITrainingRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TrainingRunResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<TrainingRunBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateTrainingRunRequest req, CancellationToken ct)
    {
        try
        {
            var run = await _runs.CreateAsync(new CreateTrainingRunCommand(req.DatasetId,
                                         req.ExpectedDatasetVersion,
                                         req.BaseArtifactId,
                                         req.LicenseConfirmed,
                                         req.Options?.ToDomain(),
                                         req.LinkedModelName),
                                     ct)
                                 .ConfigureAwait(false);
            await Send.OkAsync(run.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (TrainingRunRejectedException exception)
        {
            // Rejections are operator-facing by construction: an unconfirmed license, a checkpoint that does not fit,
            // or a dataset that is not ready.
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListTrainingRunsEndpoint(ITrainingRunService runs) : Endpoint<ListTrainingRunsRequest, ListTrainingRunsResponse>
{
    private readonly ITrainingRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListTrainingRunsRequest req, CancellationToken ct)
    {
        var page = await _runs.ListAsync(new TrainingRunQuery(req.Page, req.PageSize, req.DatasetId), ct).ConfigureAwait(false);
        await Send.OkAsync(new ListTrainingRunsResponse
        {
            Items = page.Items.Select(item => item.ToResponse()).ToArray(),
            TotalCount = page.TotalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetTrainingRunEndpoint(ITrainingRunService runs) : Endpoint<TrainingRunByIdRequest, TrainingRunResponse>
{
    private readonly ITrainingRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunByIdRequest req, CancellationToken ct)
    {
        var run = await _runs.GetAsync(req.RunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(run.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class CancelTrainingRunEndpoint(ITrainingRunService runs) : Endpoint<TrainingRunByIdRequest>
{
    private readonly ITrainingRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // The run id is the whole request and it comes from the route, so this POST has no body. Without declaring
        // that, FastEndpoints requires a JSON body and a bodyless cancel is answered with 415 instead of acting.
        Description(builder => builder.Accepts<TrainingRunByIdRequest>());
    }

    public override async Task HandleAsync(TrainingRunByIdRequest req, CancellationToken ct)
    {
        if (!await _runs.CancelAsync(req.RunId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
///     The wizard's computed starting point: options sized to this box, the VRAM estimate behind them, and the exact
///     licensing text the operator has to confirm. Read-only — it creates nothing.
/// </summary>
public sealed class GetTrainingRunDefaultsEndpoint(ITrainingOptionDefaultsCalculator defaults, ILicenseGateService licenseGate, IInstalledBaseModelLinker linker)
    : Endpoint<TrainingRunDefaultsRequest, TrainingRunDefaultsResponse>
{
    private readonly ITrainingOptionDefaultsCalculator _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
    private readonly ILicenseGateService _licenseGate = licenseGate ?? throw new ArgumentNullException(nameof(licenseGate));
    private readonly IInstalledBaseModelLinker _linker = linker ?? throw new ArgumentNullException(nameof(linker));

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
        try
        {
            var computed = await _defaults.ComputeAsync(req.BaseArtifactId, ct).ConfigureAwait(false);
            var license = await _licenseGate.GetAsync(req.BaseArtifactId, ct).ConfigureAwait(false);
            var suggestions = license is null
                ? []
                : await _linker.SuggestAsync(license.RepoId, ct).ConfigureAwait(false);
            await Send.OkAsync(computed.ToResponse(license, suggestions), ct).ConfigureAwait(false);
        }
        catch (TrainingRunRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
