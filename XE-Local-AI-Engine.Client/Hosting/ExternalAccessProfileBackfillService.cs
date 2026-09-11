namespace XE_Local_AI_Engine.Client.Hosting;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     One-time upgrade backfill for the external-access profile: a node that has already completed first-run setup and
///     carries NO external-access member at all is stamped <c>recommended</c> with all three automatic-check switches on,
///     which is exactly the behaviour it had before the profile existed. A fresh install (no administrator yet) is left
///     undecided so the first-run profile step owns the choice, and a node already holding any profile — including the
///     <c>pending</c> one first-run setup writes — is left alone.
/// </summary>
/// <remarks>
///     <para>
///         <b>A null profile alone is NOT the discriminator.</b> The legacy install this backfill exists for predates
///         every external-access member, so all four are absent. A record whose profile is null but which carries any of
///         the three switches has been touched by an operator or by a hand edit, so keying on the profile alone would
///         backfill all three switches to <c>true</c> over persisted <c>false</c> opt-outs and restart the update checks
///         and the starter-model download the operator had turned off. <c>IsUntouchedByTheFeature</c> is what guards
///         that switches-without-profile case; such a node stays undecided until an operator answers on the Node
///         Settings page. An unrecognised profile (<c>"Offline"</c>, say) never reaches here as null:
///         <c>NodeSettingsStore</c>'s <c>NormalizeExternalAccessProfile</c> loads it as <c>pending</c>, a non-null
///         profile this backfill leaves alone by the ordinary rule.
///     </para>
///     <para>
///         <b>Why the work runs in <see cref="StartAsync" /> rather than a background loop.</b>
///         <c>OllamaProviderMapBackfillService</c> is the precedent for this service's shape, scoping, idempotence and
///         swallowed failures, but its justification for being OFF the startup path — Ollama may be slow or unreachable —
///         does not apply here: this is one node-local identity read and at most one settings write, no network, so
///         blocking start costs milliseconds. Blocking is the POINT. Under minimal hosting
///         (<c>Program.CreateAppCoreAsync</c> builds with <c>WebApplication.CreateBuilder</c>) the web-host service is
///         registered last, so a user-registered hosted service's <see cref="StartAsync" /> completes before Kestrel
///         accepts a request; a background loop would leave a window in which the SPA could read
///         <c>externalAccessProfile: null</c> from a node that has been running for months and offer it a first-run
///         choice it already made. <c>Program.CreateAppAsync</c> additionally runs
///         <c>ApplyNodeIdentityMigrationsAsync</c> before <c>app.RunAsync()</c>, so the identity read below cannot race
///         the identity migration.
///     </para>
///     <para>
///         <b>Not desktop-gated.</b> An upgrading node exists on every launch mode.
///     </para>
/// </remarks>
public sealed class ExternalAccessProfileBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<ExternalAccessProfileBackfillService> logger) : IHostedService
{
    private readonly ILogger<ExternalAccessProfileBackfillService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BackfillAsync(_scopeFactory, _logger, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning(exception, "Skipping the external-access profile backfill: it could not be completed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stamps <c>recommended</c> on an upgraded node that carries no external-access member at all. Any record that
    ///     already holds one — a profile, or a single switch with the profile still null — is left exactly as it is.
    ///     Exposed as <see langword="internal" /> so it is unit-testable without standing up the hosted-service lifecycle.
    /// </summary>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();

        // The STRICT load, not LoadAsync: a present-but-unreadable settings file must not be read as "no profile yet".
        // LoadAsync would hand back a default record whose profile is null and this backfill would then "decide"
        // recommended on the strength of a corrupt file, silently re-enabling outbound checks an operator had turned
        // off. Leaving it undecided is the safe failure — the gated services keep waiting, the SPA does not show the
        // first-run step (that keys on "pending"), and Node Settings renders the profile as not chosen.
        var stored = await store.LoadStrictAsync(cancellationToken).ConfigureAwait(false);
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
        var status = await authService.GetStatusAsync(new ClaimsPrincipal(), cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);

        if (wrote)
        {
            logger.LogInformation("Backfilled the external-access profile to \"{Profile}\" on an upgraded node.",
                StoredNodeSettings.ExternalAccessProfileRecommended);
        }
    }

    /// <summary>
    ///     The legacy-install discriminator: all four external-access members absent. A null profile on its own is not
    ///     enough, because <c>NodeSettingsStore.NormalizeExternalAccessProfile</c> nulls an unrecognised profile while
    ///     keeping the switches beside it — see the type's remarks.
    /// </summary>
    private static bool IsUntouchedByTheFeature(StoredNodeSettings settings)
    {
        return settings.ExternalAccessProfile is null
            && settings.AutoCheckApplicationUpdates is null
            && settings.AutoCheckRuntimeUpdates is null
            && settings.AutoProvisionFirstRunModel is null;
    }
}
