namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Application service for i worker token refresh behavior.
/// </summary>
public interface IWorkerTokenRefreshService
{
    Task<WorkerTokenRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken = default);
}
