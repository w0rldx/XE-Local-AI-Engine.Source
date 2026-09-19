namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed class DevelopmentWorkspaceSession
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required string BaseCommit { get; init; }

    public required string RepositoryIdentityHash { get; init; }

    public required string HostWorktreePath { get; init; }

    public required string RuntimePath { get; init; }

    public required SandboxHandle SandboxHandle { get; init; }
}

internal interface IDevelopmentWorkspaceProvider
{
    Task<DevelopmentWorkspaceSession> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}
