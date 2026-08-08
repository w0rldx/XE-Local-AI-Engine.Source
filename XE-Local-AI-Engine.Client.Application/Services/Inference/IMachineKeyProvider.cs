namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     Supplies a stable, local-only machine identifier used to key inference profiles to the box they were tuned on.
///     The key is generated once and persisted in the node settings file.
/// </summary>
/// <remarks>
///     The key is LOCAL-ONLY and must NEVER be emitted in telemetry, aggregates, or logs — it exists solely so a frozen
///     profile is replayed only on the machine that produced it.
/// </remarks>
public interface IMachineKeyProvider
{
    /// <summary>
    ///     Returns the stable machine key, generating and persisting it on first use. Subsequent calls return the cached
    ///     value without re-reading or re-writing.
    /// </summary>
    Task<string> GetMachineKeyAsync(CancellationToken ct);
}
