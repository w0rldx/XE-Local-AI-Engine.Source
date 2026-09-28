namespace XE_Local_AI_Engine.Client.Services.Models;

public interface IGgufAcquisitionPreflight
{
    Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default);
}
