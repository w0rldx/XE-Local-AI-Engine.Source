namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>Answers "which container runtime, is it ready, and give me one" — the single door every application-container operation goes through.</summary>
/// <remarks>
///     It touches no persistence beyond the node settings it reads its selection from, which is what keeps it
///     usable from the operator-facing runtime endpoint, where no application instance exists to ask about.
/// </remarks>
public interface IContainerRuntimeResolver
{
    /// <summary>
    ///     Resolve the runtime. <paramref name="instanceOverride" /> outranks the stored node setting, which in turn
    ///     falls back to <see cref="ContainerRuntimeSelection.Auto" />; a resolution younger than the configured cache
    ///     window is returned without touching the daemon unless <paramref name="forceRefresh" /> says otherwise.
    /// </summary>
    Task<ContainerRuntimeResolution> ResolveAsync(ContainerRuntimeSelection? instanceOverride = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Approve the daemon currently answering as this node's daemon, replacing the pin, and return the resolution
    ///     that follows. Always probes: a confirmation applied to a cached observation would approve a daemon that is
    ///     no longer the one there.
    /// </summary>
    Task<ContainerRuntimeResolution> ConfirmDaemonIdentityAsync(string expectedDaemonId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     A runtime client for the resolved daemon. Throws <see cref="ContainerRuntimeUnavailableException" />
    ///     carrying the resolution on any status but <see cref="ContainerRuntimeStatus.Ready" />; there is no degraded
    ///     mode. The caller owns disposal.
    /// </summary>
    Task<IContainerRuntime> CreateRuntimeAsync(ContainerRuntimeSelection? instanceOverride = null,
        CancellationToken cancellationToken = default);
}
