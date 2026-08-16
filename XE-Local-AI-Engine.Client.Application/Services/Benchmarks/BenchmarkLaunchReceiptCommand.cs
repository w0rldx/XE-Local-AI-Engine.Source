namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Everything a run's durable launch-ready checkpoint records about what actually launched: the provider-owned
///     receipt and the pre-launch environment facts (both canonical JSON, encrypted at rest by the store), their
///     hashes, and the flat columns the list/compare views read without decrypting a payload.
/// </summary>
/// <remarks>
///     Deliberately strings and integers only. The receipt is assembled in the llama-server provider and serialized
///     before it reaches the application, so persisting it never drags a provider type through the store contract.
///     <see cref="PlacementOffloaded" /> / <see cref="PlacementTotal" /> are the raw layer counts the launch reported
///     (both <see langword="null" /> when the launch reported no placement).
/// </remarks>
public sealed record BenchmarkLaunchReceiptCommand(
    string ReceiptJson,
    string EnvironmentFactsJson,
    string EnvironmentFactsHash,
    string ReceiptHash,
    string EffectiveLaunchIdentity,
    string EffectiveBackend,
    int? PlacementOffloaded,
    int? PlacementTotal,
    string KvCacheTypeSource);
