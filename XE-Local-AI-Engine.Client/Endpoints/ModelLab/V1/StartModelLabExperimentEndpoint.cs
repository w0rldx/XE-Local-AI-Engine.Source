namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>202 when the job started; every refusal is a 409 decided before anything was written.</summary>
public sealed class StartModelLabExperimentEndpoint : Endpoint<StartModelLabExperimentRequest, StartModelLabExperimentResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public StartModelLabExperimentEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.Experiments);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartModelLabExperimentResponse>(StatusCodes.Status202Accepted)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartModelLabExperimentRequest request, CancellationToken ct)
    {
        var start = await _experiments.StartExperimentAsync(new ModelLabExperimentRequest
        {
            BaseArtifactId = request.BaseArtifactId,
            InstalledModelName = request.InstalledModelName,
            Prompt = request.Prompt,
            LicenseConfirmed = request.LicenseConfirmed,
            Steering = request.Steering
        }, ct);
        if (start.ExperimentId is { } id)
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null, new StartModelLabExperimentResponse
            {
                ExperimentId = id
            }));
            return;
        }

        await Send.ResultAsync(TypedResults.Conflict(new ModelLabStartBlockedResponse
        {
            Reason = start.Refusal ?? ModelLabStartRefusal.Busy,
            Message = start.RefusalMessage ?? "The experiment cannot start right now."
        }));
    }
}
