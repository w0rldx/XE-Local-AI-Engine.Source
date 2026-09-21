namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>
///     Deletes a staged artifact. Refused with 409 once it has been promoted — the registry entry owns it now.
///     Routed through the export service rather than the store so the staged bytes go with the row; the store only
///     ever removes the row.
/// </summary>
public sealed class DeleteTrainingArtifactEndpoint : Endpoint<DeleteTrainingArtifactRequest>
{
    private readonly ITrainingExportService _exports;

    public DeleteTrainingArtifactEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteTrainingArtifactRequest req, CancellationToken ct)
    {
        await _exports.DeleteArtifactAsync(req.ArtifactId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
