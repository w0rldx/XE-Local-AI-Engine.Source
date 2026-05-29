namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Initializes and recovers the worker-local <c>agent-home</c> layout (AgentHome plan Marker D, §4, §6.6). The
///     layout lives on the deterministic worker host root (not inside the sandbox); later markers copy the prepared
///     tree into the sandbox. Initialization is idempotent, self-heals a partial layout, reinitializes a stale
///     <c>initializing</c> manifest, and on an owner change kills prior runtime state and never reuses copied
///     workspace contents.
/// </summary>
internal interface IAgentHomeManifestService
{
    /// <summary>
    ///     Create or recover the worker-local AgentHome layout for <paramref name="attachKey" /> and return it in the
    ///     <see cref="AgentHomeStatus.Ready" /> state. Re-running for the same owner is a no-op when the layout is
    ///     already complete; a partial layout self-heals; an owner change wipes runtime state and reinitializes.
    /// </summary>
    Task<AgentHomeLayout> InitializeAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default);
}
