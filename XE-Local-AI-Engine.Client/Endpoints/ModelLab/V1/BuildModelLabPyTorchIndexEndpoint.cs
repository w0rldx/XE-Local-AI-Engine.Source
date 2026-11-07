namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>200 with the index (built, or from the cache); an unknown model is 404; every refusal is a 409 with a stable reason.</summary>
public sealed class BuildModelLabPyTorchIndexEndpoint : Endpoint<BuildModelLabPyTorchIndexRequest, ModelLabPyTorchIndexResponse>
{
    private readonly ModelLabPyTorchSourceService _sources;

    public BuildModelLabPyTorchIndexEndpoint(ModelLabPyTorchSourceService sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.ArchitecturePyTorch);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPyTorchIndexResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<ModelLabPyTorchBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BuildModelLabPyTorchIndexRequest req, CancellationToken ct)
    {
        // The validator ran the grammar over the decoded name; build for that same decoded name. See ModelRouteName.
        var outcome = await _sources.BuildAsync(new ModelLabPyTorchBuildRequest
        {
            ModelName = ModelRouteName.Decode(req.ModelName)!.Trim(),
            Source = req.Source!,
            BaseArtifactId = req.BaseArtifactId,
            RepoId = req.RepoId,
            Revision = req.Revision
        }, ct);
        if (outcome.Index is { } index)
        {
            await Send.OkAsync(index, ct);
            return;
        }

        await Send.ResultAsync(TypedResults.Conflict(new ModelLabPyTorchBlockedResponse
        {
            Reason = outcome.Refusal ?? ModelLabPyTorchRefusal.IndexFailed,
            Message = outcome.RefusalMessage ?? "The index could not be built."
        }));
    }
}
