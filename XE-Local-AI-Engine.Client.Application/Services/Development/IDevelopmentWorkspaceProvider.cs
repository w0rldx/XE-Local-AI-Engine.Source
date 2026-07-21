namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed record DevelopmentWorkspaceSession(
    Guid ProjectId,
    Guid TaskId,
    Guid AttemptId,
    string BaseCommit,
    string RepositoryIdentityHash,
    string HostWorktreePath,
    string RuntimePath,
    SandboxHandle SandboxHandle);

internal interface IDevelopmentWorkspaceProvider
{
    Task<DevelopmentWorkspaceSession> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        string repositoryRoot,
        CancellationToken cancellationToken = default);
}
