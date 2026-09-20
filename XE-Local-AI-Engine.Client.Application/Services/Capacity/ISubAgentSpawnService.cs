namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Spawns a model-bound sub-agent on behalf of a running agent and returns its result as a tool-result string.</summary>
/// <remarks>
///     The spawn is gated end-to-end: the per-root-invocation fan-out and cloud-spawn caps (<see cref="SpawnContext" />), then the capacity
///     gate (<see cref="ICapacityService" />) keyed on the sub-agent's <c>(model, role)</c>, then dispatch per verdict — a fitting local model
///     runs concurrently, an already-running model serializes (<see cref="ISpawnSerializer" />), and an over-budget or over-cap model is
///     REJECTED with a sanitized reason returned as the tool result, never an exception out of the caller's tool loop.
/// </remarks>
public interface ISubAgentSpawnService
{
    /// <summary>
    ///     Resolves, gates and (when admitted) runs the sub-agent for <paramref name="request" />, returning its response or a sanitized
    ///     "not possible" reason.
    /// </summary>
    /// <remarks>
    ///     Flows <paramref name="ct" /> into the inner run, so a cancelled parent cancels the child. Every expected rejection (over-cap,
    ///     no-fit, busy, missing model) returns a structured reason rather than throwing; only truly exceptional faults propagate.
    /// </remarks>
    Task<string> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct);
}
