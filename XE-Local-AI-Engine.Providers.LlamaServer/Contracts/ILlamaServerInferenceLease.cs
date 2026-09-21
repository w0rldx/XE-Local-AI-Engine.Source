namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A reference-counted lease held for the duration of ONE real inference request against a supervised
///     <c>(model, role)</c> process; disposing it releases the lease, idempotently.
/// </summary>
/// <remarks>
///     While at least one lease is held, a graceful eject waits — up to its bounded drain window — for the leases to
///     release before tearing the process down, so a normal eject never interrupts a running turn.
/// </remarks>
public interface ILlamaServerInferenceLease : IDisposable
{
    /// <summary>
    ///     <see langword="true" /> once the leased process was force-ejected by the operator while this lease was held.
    /// </summary>
    /// <remarks>
    ///     An in-flight request that fails immediately after a force-eject reads this to tell an operator eject from a
    ///     generic provider drop, so it fails as operator-ejected instead of self-healing and retrying.
    /// </remarks>
    bool WasEjected { get; }
}
