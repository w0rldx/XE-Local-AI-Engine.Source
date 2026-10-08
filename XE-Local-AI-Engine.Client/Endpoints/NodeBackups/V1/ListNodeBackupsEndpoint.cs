namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>Lists the complete node database snapshots, newest first, with the outcome of this run's pre-migration backup.</summary>
public sealed class ListNodeBackupsEndpoint : EndpointWithoutRequest<NodeBackupListResponse>
{
    private readonly INodeDbBackupService _backups;

    public ListNodeBackupsEndpoint(INodeDbBackupService backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        _backups = backups;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.NodeBackups.Backups);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new NodeBackupListResponse
            {
                Backups = [.. _backups.ListSnapshots().Select(static snapshot => snapshot.ToResponse())],
                LastAutomaticBackup = _backups.LastAutomaticBackup.ToResponse()
            },
            ct);
    }
}
