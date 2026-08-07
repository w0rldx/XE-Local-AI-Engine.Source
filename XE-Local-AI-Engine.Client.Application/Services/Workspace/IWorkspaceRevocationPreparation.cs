namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Destructive-boundary preparation invoked before an operator-selected workspace is soft-revoked. Implementations
///     must acquire the shared workspace-revocation lease and clear any live workspace rooted in the selected folder.
///     There is deliberately no permissive default: absence or failure of this service makes revocation fail closed.
/// </summary>
public interface IWorkspaceRevocationPreparation
{
    Task<IWorkspaceRevocationSession> PrepareAsync(ResolvedSelectedFolder folder, CancellationToken cancellationToken = default);
}

/// <summary>
///     Lease-bearing revocation session. Disposal releases the owner/node workspace lease, so callers must retain the
///     session until the selected-folder soft-revoke commit has completed.
/// </summary>
public interface IWorkspaceRevocationSession : IAsyncDisposable;
