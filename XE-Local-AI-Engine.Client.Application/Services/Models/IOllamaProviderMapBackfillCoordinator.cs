namespace XE_Local_AI_Engine.Client.Services.Models;

public interface IOllamaProviderMapBackfillCoordinator
{
    Task<int> BackfillAsync(CancellationToken cancellationToken = default);
}
