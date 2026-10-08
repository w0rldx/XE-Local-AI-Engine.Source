namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Persistence;

internal static class NodeBackupMapper
{
    public static NodeBackupResponse ToResponse(this NodeDbSnapshot snapshot) =>
        new()
        {
            Name = snapshot.Name,
            SizeBytes = snapshot.SizeBytes,
            CreatedUtc = snapshot.CreatedUtc
        };

    public static NodeAutomaticBackupResponse ToResponse(this NodeDbAutomaticBackupStatus status) =>
        new()
        {
            Outcome = status.Outcome,
            AtUtc = status.AtUtc,
            Error = status.Error
        };
}
