namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>Re-runs the smoke gate against an already-staged artifact and records the new verdict.</summary>
public sealed class RunTrainingArtifactSmokeEndpoint : Endpoint<TrainingArtifactByIdRequest, TrainingArtifactSmokeResponse>
{
    private readonly ITrainingExportService _exports;

    public RunTrainingArtifactSmokeEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactSmoke);
        Policies(NodeAuthorizationPolicies.Operator);
        // The artifact id is the whole request and it comes from the route, so this POST has no body. Without
        // declaring that, FastEndpoints requires a JSON body and a bodyless call is answered with 415.
        Description(builder => builder
                               .Accepts<TrainingArtifactByIdRequest>()
                               .Produces<TrainingArtifactSmokeResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(TrainingArtifactByIdRequest req, CancellationToken ct)
    {
        var result = await _exports.RunSmokeAsync(req.ArtifactId, ct);
        await Send.OkAsync(new TrainingArtifactSmokeResponse
        {
            SmokeState = result.State.ToString(),
            SmokeReason = result.Reason
        }, ct);
    }
}
