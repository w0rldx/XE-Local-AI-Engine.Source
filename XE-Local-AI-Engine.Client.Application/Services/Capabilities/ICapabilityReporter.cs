namespace XE_Local_AI_Engine.Client.Services.Capabilities;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Abstraction for capability reporter behavior.
/// </summary>
public interface ICapabilityReporter
{
    Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task ReportToApiAsync(CancellationToken cancellationToken = default);

    Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default);
}
