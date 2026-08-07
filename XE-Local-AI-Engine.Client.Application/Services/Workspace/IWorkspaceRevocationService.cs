namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Operator orchestration boundary for idempotent selected-workspace revocation.</summary>
public interface IWorkspaceRevocationService
{
    /// <summary>
    ///     Clears any live workspace and then soft-revokes the selected folder. Unknown and already-revoked ids are
    ///     treated as successful no-ops so callers cannot distinguish them.
    /// </summary>
    Task RevokeAsync(string workspaceId, CancellationToken cancellationToken = default);
}
