namespace XE_Local_AI_Engine.Client.Hosting;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     One-time upgrade backfill for the navigation mode: a node whose operator has already been through first-run
///     onboarding and carries no <c>UiMode</c> is stamped <c>advanced</c>, which is exactly the navigation it showed
///     before the mode existed. A node still inside first-run onboarding is left undecided, so the first-run mode step
///     owns the choice, and a node that already holds a mode is left alone.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the discriminator is not "an administrator exists".</b> That is the moment first-run onboarding
///         STARTS, not the moment it finishes: the setup endpoint persists the administrator and stamps the
///         external-access profile <c>pending</c> in the same call, and the operator then answers the external-access
///         step and the mode step. Keying on the administrator alone would stamp <c>advanced</c> on a brand-new node
///         that restarted while its operator was still looking at the external-access chooser, and that operator would
///         never be asked. So the discriminator is the SUCCESSOR state of the step before this one: an administrator
///         exists AND the external-access profile is anything other than <c>pending</c>.
///     </para>
///     <para>
///         That deliberately includes a null profile, which is what an upgraded node carries until
///         <see cref="ExternalAccessProfileBackfillService" /> stamps it, and what a node that answered its switches by
///         hand keeps for good (see that service's remarks). Both are installs whose operator has long since finished
///         whatever onboarding existed for them, so both want <c>advanced</c> — and reading the state rather than the
///         other service's output is what keeps these two backfills independent of their start order.
///     </para>
///     <para>
///         <b>The one window this leaves.</b> A node that is restarted between the external-access answer and the mode
///         answer is stamped <c>advanced</c> and not asked again. Closing it would need a third <c>pending</c>-style
///         literal, which the mode does not otherwise need; the cost of leaving it open is that such an operator sees
///         the full navigation and changes it on the Node Settings page, which is what the step's own copy tells them.
///     </para>
///     <para>
///         <b>Why the work runs in <see cref="StartAsync" />.</b> Same reason as
///         <see cref="ExternalAccessProfileBackfillService" />: one node-local identity read and at most one settings
///         write, no network, and blocking start is the point — a background loop would leave a window in which the SPA
///         could read <c>uiMode: null</c> from a node that has been running for months and send its operator to a
///         first-run step they already passed. Not desktop-gated; an upgrading node exists on every launch mode.
///     </para>
/// </remarks>
public sealed class UiModeBackfillService : IHostedService
{
    private readonly ILogger<UiModeBackfillService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public UiModeBackfillService(IServiceScopeFactory scopeFactory, ILogger<UiModeBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BackfillAsync(_scopeFactory, _logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down before it finished starting — the next boot retries.
        }
        catch (Exception exception)
        {
            // This runs on the startup path, so an unswallowed failure would stop the host from starting at all. A node
            // that cannot read its identity database or its settings file must still come up; the backfill is idempotent
            // and the next boot retries.
            // Warning, not Debug: Debug is off in production, so a real defect here would leave no trace at all.
            _logger.LogWarning(exception, "Skipping the interface-mode backfill: it could not be completed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stamps <c>advanced</c> on a node whose operator has finished first-run onboarding and has no mode yet. Any
    ///     record that already holds a mode is left exactly as it is. Exposed as <see langword="internal" /> so it is
    ///     unit-testable without standing up the hosted-service lifecycle.
    /// </summary>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();

        // The STRICT load, not LoadAsync: a present-but-unreadable settings file must not be read as "no mode yet". The
        // safe failure is leaving it undecided — the navigation reads an absent mode as advanced anyway, so nothing
        // disappears, and repairing the file lets the next boot decide properly.
        var stored = await store.LoadStrictAsync(cancellationToken);
        if (stored is null)
        {
            logger.LogWarning("Skipping the interface-mode backfill: the node settings file could not be read, so the "
                              + "interface mode stays undecided and the navigation shows every entry.");
            return;
        }

        if (!IsBackfillable(stored))
        {
            return;
        }

        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();
        var status = await authService.GetStatusAsync(new ClaimsPrincipal(), cancellationToken);
        if (status.SetupRequired)
        {
            // No administrator, so this is a fresh install and the first-run mode step owns the decision.
            return;
        }

        var wrote = false;
        _ = await store.UpdateAsync(latest =>
            {
                // Re-checked inside the mutation, which runs under the store's own lock: a mode written between the load
                // above and this point must not be overwritten.
                if (!IsBackfillable(latest))
                {
                    return latest;
                }

                wrote = true;
                return latest with
                {
                    UiMode = StoredNodeSettings.UiModeAdvanced
                };
            },
            cancellationToken);

        if (wrote)
        {
            logger.LogInformation("Backfilled the interface mode to \"{UiMode}\" on an upgraded node.", StoredNodeSettings.UiModeAdvanced);
        }
    }

    /// <summary>
    ///     No mode yet, and the operator is past the external-access step — see the type's remarks for why <c>pending</c>
    ///     is the one profile that blocks the stamp and why a null profile does not.
    /// </summary>
    private static bool IsBackfillable(StoredNodeSettings settings)
    {
        return settings.UiMode is null
               && !string.Equals(settings.ExternalAccessProfile, StoredNodeSettings.ExternalAccessProfilePending, StringComparison.Ordinal);
    }
}
