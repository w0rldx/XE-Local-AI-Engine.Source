namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The process-wide pending-footprint ledger and decide-commit gate behind the capacity service.</summary>
/// <remarks>
///     The live byte snapshot does not yet reflect a spawn already admitted but still loading, so two fitting models could read the same free
///     budget and both pass. The ledger closes that TOCTOU window: one gate serializes read-decide-reserve, and the bytes of in-flight spawns
///     are tracked until the child exits and the caller disposes the handle the decision carries. It is a singleton, so the gate and the
///     reservations survive the per-spawn DI scopes the capacity service is resolved in and are shared by every concurrent spawn on the node.
/// </remarks>
public interface IPendingFootprintLedger
{
    /// <summary>Acquires the process-wide decide-commit gate.</summary>
    /// <remarks>
    ///     The capacity service holds the returned handle for the whole read-decide-reserve sequence, so two concurrent local decisions cannot
    ///     both pass on the same snapshot. Disposing it releases the gate, once the decision is committed (a reservation taken) or abandoned.
    /// </remarks>
    Task<IDisposable> EnterDecisionAsync(CancellationToken ct);

    /// <summary>Total bytes currently reserved by in-flight (admitted-not-yet-released) spawns. Read under the gate.</summary>
    ResourceFootprint Reserved { get; }

    /// <summary>
    ///     Reserves <paramref name="bytes" /> against the ledger and returns a handle that releases the reservation on
    ///     dispose (idempotent). Call only while holding the decision gate, after deciding the model fits.
    /// </summary>
    IDisposable Reserve(ResourceFootprint footprint);
}
