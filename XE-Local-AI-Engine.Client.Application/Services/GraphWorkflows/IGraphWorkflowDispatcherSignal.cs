namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Tells the dispatcher a run has changed. Every mutating command calls it AFTER its commit, so the tick that
///     follows reads the committed row rather than racing it.
///     <para>
///         Latency only: a dropped signal costs at most one sweep interval and never correctness, which is what lets the
///         channel behind it drop writes rather than block a committing caller.
///     </para>
/// </summary>
internal interface IGraphWorkflowDispatcherSignal
{
    void Signal(Guid runId);
}

/// <summary>
///     The signal a host without a dispatcher resolves. Registered <c>TryAddSingleton</c>, so the dispatcher's own
///     registration replaces it the moment that slice lands — and until then a run start commits and sits
///     <c>Pending</c>, which is exactly what a node with no tick loop should look like.
/// </summary>
internal sealed class NoOpGraphWorkflowDispatcherSignal : IGraphWorkflowDispatcherSignal
{
    public void Signal(Guid runId)
    {
        // Nothing is listening yet. The absent body is the whole implementation, not a placeholder for one.
    }
}
