namespace XE_Local_AI_Engine.Client.Hosting;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     One-time upgrade backfill for the navigation mode: a node whose operator has already been through first-run
///     onboarding and carries no <c>UiMode</c> is stamped <c>advanced</c>.
/// </summary>
/// <remarks>
///     A node still inside first-run onboarding is left undecided, so the first-run mode step owns the choice, and a
///     node that already holds a mode is left alone. The discriminator is the SUCCESSOR state of the step before this
///     one — an administrator exists AND the external-access profile is anything other than <c>pending</c> — which is
///     what keeps this independent of <see cref="ExternalAccessProfileBackfillService" />'s start order. Not
///     desktop-gated. See docs/wiki/11-hosting-and-deployment.md ("Upgrade backfills and their discriminators").
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
            // This runs on the startup path, so an unswallowed failure would stop the host starting at all; the backfill is
            // idempotent and the next boot retries. Warning, not Debug: Debug is off in production and would leave no trace.
            _logger.LogWarning(exception, "Skipping the interface-mode backfill: it could not be completed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stamps <c>advanced</c> on a node whose operator has finished first-run onboarding and has no mode yet.
    /// </summary>
    /// <remarks>
    ///     Any record that already holds a mode is left exactly as it is. Exposed as <see langword="internal" /> so it
    ///     is unit-testable without standing up the hosted-service lifecycle.
    /// </remarks>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();

        // The STRICT load, not LoadAsync: a present-but-unreadable settings file must not read as "no mode yet". Undecided
        // is the safe failure — navigation reads an absent mode as advanced, and repairing the file lets the next boot decide.
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
