namespace XE_Local_AI_Engine.Client.Hosting;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     One-time upgrade backfill that stamps the external-access profile <c>recommended</c> on a node that predates the
///     feature and carries none of its members.
/// </summary>
/// <remarks>
///     A null profile alone is NOT the discriminator: a record with a switch but no profile was touched by an operator,
///     and backfilling it would restart update checks and downloads that were turned off, so
///     <see cref="IsUntouchedByTheFeature" /> guards that case and such a node stays undecided. The work runs in
///     <see cref="StartAsync" /> rather than a background loop, and reads settings STRICTLY. Not desktop-gated. See
///     docs/wiki/11-hosting-and-deployment.md ("Upgrade backfills and their discriminators").
/// </remarks>
public sealed class ExternalAccessProfileBackfillService : IHostedService
{
    private readonly ILogger<ExternalAccessProfileBackfillService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ExternalAccessProfileBackfillService(IServiceScopeFactory scopeFactory,
        ILogger<ExternalAccessProfileBackfillService> logger)
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
            _logger.LogWarning(exception, "Skipping the external-access profile backfill: it could not be completed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stamps <c>recommended</c> on an upgraded node that carries no external-access member at all.
    /// </summary>
    /// <remarks>
    ///     Any record that already holds one — a profile, or a single switch with the profile still null — is left
    ///     exactly as it is. Exposed as <see langword="internal" /> so it is unit-testable without standing up the
    ///     hosted-service lifecycle.
    /// </remarks>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();

        // The STRICT load, not LoadAsync: a present-but-unreadable settings file must not read as "no profile yet", or this backfill
        // decides recommended on a corrupt file and re-enables outbound checks an operator turned off. Undecided is the safe failure.
        var stored = await store.LoadStrictAsync(cancellationToken);
        if (stored is null)
        {
            logger.LogWarning("Skipping the external-access profile backfill: the node settings file could not be read, "
                              + "so the profile stays undecided. Repair or delete node-settings.json to choose one.");
            return;
        }

        if (!IsUntouchedByTheFeature(stored))
        {
            if (stored.ExternalAccessProfile is null)
            {
                // A switch without a profile: an operator's opt-out, or a hand-edited profile the store normalised away.
                // Backfilling would flip those switches back on, so the node stays undecided until someone answers.
                logger.LogWarning("Skipping the external-access profile backfill: this node carries external-access "
                                  + "switches but no recognised profile, so the profile stays undecided and its switches are kept as "
                                  + "they are. Choose a preset on the Node Settings page.");
            }

            return;
        }

        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();
        var status = await authService.GetStatusAsync(new ClaimsPrincipal(), cancellationToken);
        if (status.SetupRequired)
        {
            // No administrator, so this is a fresh install and the first-run profile step owns the decision.
            return;
        }

        var wrote = false;
        _ = await store.UpdateAsync(latest =>
            {
                // Re-checked inside the mutation, which runs under the store's own lock: a concurrent write landing
                // between the load above and this point must not be overwritten.
                if (!IsUntouchedByTheFeature(latest))
                {
                    return latest;
                }

                wrote = true;
                return latest with
                {
                    ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileRecommended,
                    AutoCheckApplicationUpdates = true,
                    AutoCheckRuntimeUpdates = true,
                    AutoProvisionFirstRunModel = true
                };
            },
            cancellationToken);

        if (wrote)
        {
            logger.LogInformation("Backfilled the external-access profile to \"{Profile}\" on an upgraded node.",
                StoredNodeSettings.ExternalAccessProfileRecommended);
        }
    }

    /// <summary>
    ///     The legacy-install discriminator: all four external-access members absent.
    /// </summary>
    /// <remarks>
    ///     A null profile on its own is not enough, because
    ///     <c>NodeSettingsStore.NormalizeExternalAccessProfile</c> nulls an unrecognised profile while keeping the
    ///     switches beside it — see the type's remarks.
    /// </remarks>
    private static bool IsUntouchedByTheFeature(StoredNodeSettings settings)
    {
        return settings.ExternalAccessProfile is null
               && settings.AutoCheckApplicationUpdates is null
               && settings.AutoCheckRuntimeUpdates is null
               && settings.AutoProvisionFirstRunModel is null;
    }
}
