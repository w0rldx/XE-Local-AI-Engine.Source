namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Resolves the fine-grained runtime provider (<see cref="AgentUsageProviders" />) that served a turn, for the
///     token-usage ledger. Consulted at terminalization from the run's resolved model id. It is a best-effort attribution
///     that must NEVER throw or stall terminalization: any failure, timeout, ambiguity, or a null/blank model degrades to
///     <see cref="AgentUsageProviders.Unknown" />. Because it resolves at terminalize-time it reflects the selection then;
///     a mid-turn sign-in/out is the accepted trade for keeping the resolution off the streaming hot path.
/// </summary>
public interface IUsageProviderResolver
{
    /// <summary>
    ///     Classifies the provider that served <paramref name="modelName" /> into a canonical usage label. Never throws;
    ///     returns <see cref="AgentUsageProviders.Unknown" /> for a null/blank model or on any resolution failure/timeout.
    /// </summary>
    Task<string> ResolveAsync(string? modelName, CancellationToken cancellationToken = default);
}
