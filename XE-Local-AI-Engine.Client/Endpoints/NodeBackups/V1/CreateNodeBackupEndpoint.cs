namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Takes a node database snapshot now, with the same naming and retention as the pre-migration one. Body-less.
/// </summary>
/// <remarks>
///     201 with the new row; 409 while another snapshot runs or when the database is not a snapshot-able file; 507 when the
///     free-space guard refuses.
/// </remarks>
public sealed class CreateNodeBackupEndpoint : EndpointWithoutRequest<NodeBackupResponse>
{
    private readonly INodeDbBackupService _backups;

    public CreateNodeBackupEndpoint(INodeDbBackupService backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        _backups = backups;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBackups.Backups);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<NodeBackupResponse>(StatusCodes.Status201Created)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status507InsufficientStorage));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await _backups.CreateSnapshotAsync(ct);
        switch (result.Status)
        {
            case NodeDbSnapshotCreateStatus.Created:
                await Send.ResponseAsync(result.Snapshot!.ToResponse(), StatusCodes.Status201Created, ct);
                return;
            case NodeDbSnapshotCreateStatus.Busy:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A database backup is already running."));
                return;
            case NodeDbSnapshotCreateStatus.Unsupported:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This node's database is not a file that can be backed up."));
                return;
            default:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status507InsufficientStorage,
                    title: "There is not enough free disk space for a database backup."));
                return;
        }
    }
}
