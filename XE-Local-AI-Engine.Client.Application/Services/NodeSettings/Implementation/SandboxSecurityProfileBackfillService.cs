namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     One-time upgrade backfill for the sandbox security profile: a node whose operator has already been through first-run
///     onboarding and carries no profile is stamped <c>low</c>, today's behaviour, so an upgraded node is never asked.
/// </summary>
/// <remarks>
///     The discriminator is <see cref="UiModeBackfillService" />'s, deliberately: an administrator exists AND the external-access
///     profile is anything other than <c>pending</c>. That keeps the three backfills independent of each other's start order. A fresh
///     node is stamped <c>pending</c> by setup and so is never touched here, and a node that already holds a literal is left alone.
///     Not desktop-gated. See ADR 0020 and docs/wiki/11-hosting-and-deployment.md ("Upgrade backfills and their discriminators").
/// </remarks>
public sealed class SandboxSecurityProfileBackfillService : IHostedService
{
    private readonly ILogger<SandboxSecurityProfileBackfillService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SandboxSecurityProfileBackfillService(IServiceScopeFactory scopeFactory, ILogger<SandboxSecurityProfileBackfillService> logger)
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
            // Startup path: an unswallowed failure would stop the host starting at all. The backfill is idempotent and the next boot
            // retries; an undecided profile reads as low meanwhile, which is exactly what the backfill would have written.
            _logger.LogWarning(exception, "Skipping the sandbox security profile backfill: it could not be completed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Stamps <c>low</c> on a node whose operator has finished first-run onboarding and has no profile yet.</summary>
    /// <remarks>Exposed as <see langword="internal" /> so it is unit-testable without standing up the hosted-service lifecycle.</remarks>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INodeSettingsStore>();

        // The STRICT load: a present-but-unreadable settings file must not read as "no profile yet" and be overwritten.
        var stored = await store.LoadStrictAsync(cancellationToken);
        if (stored is null)
        {
            logger.LogWarning("Skipping the sandbox security profile backfill: the node settings file could not be read, so the "
                              + "profile stays undecided and every sandbox runs under the low profile.");
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
            // No administrator, so this is a fresh install and the first-run chooser owns the decision.
            return;
        }

        var wrote = false;
        _ = await store.UpdateAsync(latest =>
            {
                // Re-checked under the store's own lock: a profile written since the load above must not be overwritten.
                if (!IsBackfillable(latest))
                {
                    return latest;
                }

                wrote = true;
                return latest with
                {
                    SandboxSecurityProfile = StoredNodeSettings.SandboxSecurityProfileLow
                };
            },
            cancellationToken);

        if (wrote)
        {
            logger.LogInformation("Backfilled the sandbox security profile to \"{SandboxSecurityProfile}\" on an upgraded node.",
                StoredNodeSettings.SandboxSecurityProfileLow);
        }
    }

    /// <summary>No profile yet, and the operator is past the external-access step (the type's remarks say why <c>pending</c> blocks).</summary>
    private static bool IsBackfillable(StoredNodeSettings settings)
    {
        return settings.SandboxSecurityProfile is null
               && !string.Equals(settings.ExternalAccessProfile, StoredNodeSettings.ExternalAccessProfilePending, StringComparison.Ordinal);
    }
}
