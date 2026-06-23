namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     The process-wide pending-footprint ledger and decide-commit gate behind the capacity service. It exists because
///     the byte budget is read from a live snapshot (hardware profile + running models) that does NOT yet reflect a
///     concurrent spawn that was just admitted but whose model has not finished loading: two different fitting models
///     could each read the same free budget and both pass (TOCTOU). The ledger closes that window by serializing the
///     read-decide-reserve sequence under one gate and tracking the bytes of in-flight (admitted-not-yet-resident)
///     spawns so a second concurrent decision sees the first reservation. Reservations are released when the spawned
///     child exits (the caller disposes the handle the decision carries).
/// </summary>
/// <remarks>
///     Singleton — it must survive across the per-spawn DI scopes the capacity service is resolved in, so its
///     reservations and gate are shared by every concurrent spawn on the node.
/// </remarks>
public interface IPendingFootprintLedger
{
    /// <summary>
    ///     Acquires the process-wide decide-commit gate. The capacity service holds the returned handle for the whole
    ///     read-decide-reserve sequence so two concurrent local decisions cannot both pass on the same snapshot. The
    ///     handle is disposed (gate released) once the decision is committed (a reservation taken) or abandoned.
    /// </summary>
    Task<IDisposable> EnterDecisionAsync(CancellationToken ct);

    /// <summary>Total bytes currently reserved by in-flight (admitted-not-yet-released) spawns. Read under the gate.</summary>
    long ReservedBytes { get; }

    /// <summary>
    ///     Reserves <paramref name="bytes" /> against the ledger and returns a handle that releases the reservation on
    ///     dispose (idempotent). Call only while holding the decision gate, after deciding the model fits.
    /// </summary>
    IDisposable Reserve(long bytes);
}
