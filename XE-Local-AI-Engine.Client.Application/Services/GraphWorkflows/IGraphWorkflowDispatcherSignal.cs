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
