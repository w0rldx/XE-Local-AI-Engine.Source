namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Initializes and recovers the worker-local <c>agent-home</c> layout.
/// </summary>
/// <remarks>
///     The layout lives on the deterministic worker host root, not inside the sandbox — workspace-copy steps copy
///     the prepared tree in. Initialization is idempotent, self-heals a partial layout, reinitializes a stale
///     <c>initializing</c> manifest, and on an owner change kills prior runtime state and never reuses copied
///     workspace contents.
/// </remarks>
internal interface IAgentHomeManifestService
{
    /// <summary>
    ///     Create or recover the worker-local AgentHome layout for <paramref name="attachKey" /> and return it in
    ///     the <see cref="AgentHomeStatus.Ready" /> state.
    /// </summary>
    /// <remarks>
    ///     Re-running for the same owner is a no-op when the layout is already complete, a partial layout self-heals,
    ///     and an owner change wipes runtime state and reinitializes.
    /// </remarks>
    Task<AgentHomeLayout> InitializeAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default);
}
