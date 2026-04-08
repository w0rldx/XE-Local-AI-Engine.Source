namespace XE_Local_AI_Engine.Services.Capabilities;

using XE_Local_AI_Engine.Models;

public interface ICapabilityReporter
{
    Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task ReportToApiAsync(CancellationToken cancellationToken = default);

    Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default);
}
