namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The wait-until-decided gate the three automatic outbound checks share: it returns once the node's
///     external-access profile has been chosen, and blocks until then.
/// </summary>
/// <remarks>
///     A stateless function over <see cref="INodeRuntimeSettings" /> and a <see cref="TimeProvider" />, needing no
///     interface, lifetime or registration. Both <see langword="null" /> and <c>ExternalAccessProfilePending</c> are UNDECIDED:
///     <see langword="null" /> means no administrator exists yet, so there is nobody to ask, and <c>pending</c> means an
///     administrator exists and the first-run choice has not been made. After the boot backfill an upgraded node is
///     always decided before the host accepts a request, so this wait is only ever paid on a fresh install.
/// </remarks>
public static class ExternalAccessGate
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Returns once the external-access profile is decided, or throws <see cref="OperationCanceledException" /> when
    ///     <paramref name="cancellationToken" /> fires — which every caller already treats as shutdown.
    /// </summary>
    public static async Task WaitUntilDecidedAsync(INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (IsDecided(await runtimeSettings.GetExternalAccessProfileAsync(cancellationToken)))
        {
            return;
        }

        // simplified: a poll, not a signal — the decision arrives from an HTTP save on another thread and nothing in the
        // node-settings stack publishes a change notification today. Swap for a write-side signal if boot latency matters.
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (IsDecided(await runtimeSettings.GetExternalAccessProfileAsync(cancellationToken)))
            {
                return;
            }
        }
    }

    private static bool IsDecided(string? profile)
    {
        return profile is not null
               && !string.Equals(profile, StoredNodeSettings.ExternalAccessProfilePending, StringComparison.Ordinal);
    }
}
