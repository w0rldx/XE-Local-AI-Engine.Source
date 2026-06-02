namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     Idempotent startup task that seeds the code-defined approved utility image descriptors
///     (<see cref="ApprovedUtilityImageCatalog" />) into the SQLite registry once at startup. Every descriptor's image
///     reference is re-validated through the <see cref="ApprovedImageReferenceValidator" /> before it is seeded: a
///     descriptor whose code reference fails validation is logged and SKIPPED, so a bad code reference can never seed.
///     The upsert preserves an operator's <c>Enabled</c> toggle, so re-running this on every startup is safe. Seeding is
///     best-effort: a failure is logged and swallowed so the node still starts.
/// </summary>
public sealed class ApprovedUtilityImageSeeder : IHostedService
{
    private readonly ApprovedImageReferenceValidator _referenceValidator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApprovedUtilityImageSeeder> _logger;

    public ApprovedUtilityImageSeeder(
        IServiceScopeFactory scopeFactory,
        ApprovedImageReferenceValidator referenceValidator,
        ILogger<ApprovedUtilityImageSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _referenceValidator = referenceValidator ?? throw new ArgumentNullException(nameof(referenceValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IApprovedUtilityImageStore>();

            foreach (var descriptor in ApprovedUtilityImageCatalog.Descriptors)
            {
                var validation = _referenceValidator.Validate(descriptor.ImageReference);
                if (!validation.IsValid)
                {
                    // A bad CODE reference must never seed — skip it loudly. The image reference is not logged (defence
                    // in depth), only the descriptor id and the sanitized reason.
                    _logger.LogError(
                        "Skipping approved utility image seed for {ApprovedImageId}: image reference failed validation ({Error}).",
                        descriptor.ApprovedImageId,
                        validation.Error);
                    continue;
                }

                _ = await store.UpsertSeedAsync(descriptor, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Seeded approved utility image descriptor {ApprovedImageId}.", descriptor.ApprovedImageId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            // Seeding is best-effort: a node must start even if the registry seed fails. The operator can re-trigger on
            // the next startup once the underlying issue clears.
            _logger.LogWarning(ex, "Approved utility image seeding failed at startup; the registry may be incomplete until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
