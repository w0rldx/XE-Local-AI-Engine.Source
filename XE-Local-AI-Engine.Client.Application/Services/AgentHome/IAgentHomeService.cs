namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Orchestrates one node-scoped AgentHome lifecycle under the shared owner-node execution lease. Preparation remains
///     separately timed internally, but no caller can split preparation from execution and release the lease between them.
/// </summary>
internal interface IAgentHomeService
{
    /// <summary>
    ///     The single lifecycle entry the gateway calls (AgentHome gateway): resolves owner/node identity once, acquires the
    ///     shared execution lease keyed by that owner-node. A run requested while another owner-node operation holds the
    ///     lease is rejected with <see cref="AgentHomeBusyException" />, not queued. Preparation and execution complete
    ///     before the lease is released.
    /// </summary>
    Task<AgentHomeRunResult> RunLifecycleAsync(AgentHomeRunLifecycleRequest request, CancellationToken cancellationToken = default);
}
