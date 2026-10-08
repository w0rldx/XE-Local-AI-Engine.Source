namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Stages a restore of one listed snapshot and stops the node once the 202 completes; the next start applies it.
/// </summary>
/// <remarks>
///     400 for a name that is not a snapshot file name, 404 for one that is not listed. 409 outside local mode (only a local-mode
///     start applies a staged restore), while a backup runs, or when the snapshot is a link, fails <c>PRAGMA quick_check</c> or
///     records a migration this binary does not ship.
/// </remarks>
public sealed class RestoreNodeBackupEndpoint : Endpoint<RestoreNodeBackupRequest, RestoreNodeBackupResponse>
{
    private readonly INodeDbBackupService _backups;
    private readonly NodeLaunchContext _launchContext;
    private readonly AppUpdateShutdownCoordinator _shutdownCoordinator;

    public RestoreNodeBackupEndpoint(INodeDbBackupService backups,
        NodeLaunchContext launchContext,
        AppUpdateShutdownCoordinator shutdownCoordinator)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(launchContext);
        ArgumentNullException.ThrowIfNull(shutdownCoordinator);
        _backups = backups;
        _launchContext = launchContext;
        _shutdownCoordinator = shutdownCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBackups.Restore);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: without this the default JSON-only Accepts metadata answers a body-less request 415.
        Description(builder => builder.Accepts<RestoreNodeBackupRequest>()
                                      .Produces<RestoreNodeBackupResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemFE(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RestoreNodeBackupRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (_backups.ResolveSnapshotPath(req.Name) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!_launchContext.IsLocalMode)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "A restore is applied only when the node runs in local mode."));
            return;
        }

        var staged = await _backups.StageRestoreAsync(req.Name, ct);
        switch (staged.Status)
        {
            case NodeDbRestoreStageStatus.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case NodeDbRestoreStageStatus.Busy:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A database backup is running; try again when it has finished."));
                return;
            case NodeDbRestoreStageStatus.Refused:
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The backup cannot be restored.", detail: staged.Reason));
                return;
        }

        _shutdownCoordinator.StopAfterResponseCompleted(HttpContext.Response);
        await Send.ResultAsync(Results.Accepted(value: new RestoreNodeBackupResponse
        {
            NodeStopping = true
        }));
    }
}
