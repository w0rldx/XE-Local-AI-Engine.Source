namespace XE_Local_AI_Engine.Services.Auth;

using XE_Local_AI_Engine.Models;

public interface IPairingService
{
    Task<PairClientResponse> PairAsync(string pairingToken, CancellationToken cancellationToken = default);

    Task UnpairAsync(CancellationToken cancellationToken = default);
}
