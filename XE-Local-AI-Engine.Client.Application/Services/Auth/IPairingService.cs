namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Models;

public interface IPairingService
{
    Task<PairClientResponse> PairAsync(string pairingToken, CancellationToken cancellationToken = default);

    Task UnpairAsync(CancellationToken cancellationToken = default);
}
