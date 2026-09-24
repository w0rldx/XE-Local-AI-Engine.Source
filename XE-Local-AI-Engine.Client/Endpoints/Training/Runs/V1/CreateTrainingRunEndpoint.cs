namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
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
