namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     Starts a run. The license confirmation is enforced here AND in the store's create transaction — the endpoint so
///     the operator gets a 400 rather than a 500, the store so no other caller can bypass it.
/// </summary>
public sealed class CreateTrainingRunEndpoint : Endpoint<CreateTrainingRunRequest, TrainingRunResponse>
{
    private readonly ITrainingRunService _runs;

    public CreateTrainingRunEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TrainingRunResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateTrainingRunRequest req, CancellationToken ct)
    {
        // TrainingRunRejectedException reaches the global DomainValidationExceptionHandler as a 400, its rejections being operator-facing by construction. The store's own
        // refusals are a different family: VersionConflict (ExpectedDatasetVersion catches it), DatasetNotReady and BaseArtifactNotReady leave via TrainingExceptionHandler as a 409.
        var run = await _runs.CreateAsync(new CreateTrainingRunCommand
        {
            DatasetId = req.DatasetId,
            ExpectedDatasetVersion = req.ExpectedDatasetVersion,
            BaseArtifactId = req.BaseArtifactId,
            LicenseConfirmed = req.LicenseConfirmed,
            Options = req.Options?.ToDomain(),
            LinkedModelName = req.LinkedModelName
        },
                                 ct);
        await Send.OkAsync(run.ToResponse(), ct);
    }
}

public sealed class ListTrainingRunsEndpoint : Endpoint<ListTrainingRunsRequest, ListTrainingRunsResponse>
{
    private readonly ITrainingRunService _runs;

    public ListTrainingRunsEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListTrainingRunsRequest req, CancellationToken ct)
    {
        var page = await _runs.ListAsync(new TrainingRunQuery { Page = req.Page, PageSize = req.PageSize, DatasetId = req.DatasetId }, ct);
        await Send.OkAsync(new ListTrainingRunsResponse
        {
            Items = page.Items.Select(item => item.ToResponse()).ToArray(),
            TotalCount = page.TotalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}

public sealed class GetTrainingRunEndpoint : Endpoint<TrainingRunByIdRequest, TrainingRunResponse>
{
    private readonly ITrainingRunService _runs;

    public GetTrainingRunEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunByIdRequest req, CancellationToken ct)
    {
        var run = await _runs.GetAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(run.ToResponse(), ct);
    }
}

public sealed class CancelTrainingRunEndpoint : Endpoint<TrainingRunByIdRequest>
{
    private readonly ITrainingRunService _runs;

    public CancelTrainingRunEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

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
        if (!await _runs.CancelAsync(req.RunId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

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
