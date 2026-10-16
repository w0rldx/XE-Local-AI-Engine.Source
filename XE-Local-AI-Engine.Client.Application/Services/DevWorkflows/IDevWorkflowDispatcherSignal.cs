namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Tells the dispatcher a run has changed. Every mutating command calls it AFTER its commit, so the tick that
///     follows reads the committed row rather than racing it.
/// </summary>
/// <remarks>
///     Latency only: a dropped signal costs at most one sweep interval and never correctness, which is what lets the
///     channel behind it drop writes rather than block a committing caller.
/// </remarks>
internal interface IDevWorkflowDispatcherSignal
{
    void Signal(Guid runId);

    /// <summary>
    ///     Advances the run by one tick now, serialized with the dispatcher's own ticks, so the caller reads what its command led to.
    ///     False when the tick could not take its turn within <paramref name="gateWait" />; the tick itself is never cancelled once it starts.
    /// </summary>
    Task<bool> TryAdvanceAsync(Guid runId, TimeSpan gateWait, CancellationToken cancellationToken);
}
