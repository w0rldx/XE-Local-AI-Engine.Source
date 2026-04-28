namespace XE_Local_AI_Engine.Client.Services.Auth;

public interface IWorkerTokenRefreshService
{
    Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default);
}
