namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>One source file an index names, from the installed runtime; any other path, a changed file or a missing runtime is a 409.</summary>
public sealed class GetModelLabPyTorchSourceEndpoint : Endpoint<GetModelLabPyTorchSourceRequest, ModelLabPyTorchSourceFile>
{
    private readonly ModelLabPyTorchSourceService _sources;

    public GetModelLabPyTorchSourceEndpoint(ModelLabPyTorchSourceService sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.PyTorchSource);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPyTorchSourceFile>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<ModelLabPyTorchBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(GetModelLabPyTorchSourceRequest req, CancellationToken ct)
    {
        var read = await _sources.ReadSourceAsync(req.Index!, req.File!, ct);
        if (read.Source is { } source)
        {
            await Send.OkAsync(source, ct);
            return;
        }

        await Send.ResultAsync(TypedResults.Conflict(new ModelLabPyTorchBlockedResponse
        {
            Reason = read.Refusal ?? ModelLabPyTorchRefusal.SourceNotInIndex,
            Message = read.RefusalMessage ?? "That file is not served."
        }));
    }
}
