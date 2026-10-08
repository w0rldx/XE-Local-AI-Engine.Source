namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>A restore names a bare snapshot file name; a path, a separator or another prefix never reaches the file system.</summary>
public sealed class RestoreNodeBackupRequestValidator : Validator<RestoreNodeBackupRequest>
{
    public RestoreNodeBackupRequestValidator()
    {
        RuleFor(static request => request.Name)
            .Must(NodeDbRestoreStaging.IsValidSnapshotName)
            .WithMessage("Not a backup name.");
    }
}
