namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

/// <summary>
///     Idempotent startup task that seeds ONE enabled, on-demand <see cref="ModelRecommendationCheckHandler" /> schedule
///     so the React model-fit "Refresh now" button works without the operator hand-creating a schedule first. The seeded
///     definition uses <see cref="Persistence.Entities.ScheduleKind.Manual" />: a durable Quartz job with no trigger that
///     never auto-fires — it is fired only on demand via <c>TriggerNowAsync</c>.
///     <para>
///         <b>Idempotent + self-healing.</b> It seeds only when NO non-deleted definition already references the
///         <c>model-recommendation-check</c> template, so re-runs never duplicate it. If an operator deletes the seeded
///         definition, the next startup re-seeds it.
///     </para>
///     <para>
///         <b>Best-effort.</b> A node must still start even if seeding fails (e.g. a transient DB error), so the expected
///         failures are logged and swallowed; the next startup re-attempts once the underlying issue clears. Registered in
///         the Client host AFTER the Quartz scheduler so the scheduler factory/job store are available when this runs.
///     </para>
/// </summary>
public sealed class ModelRecommendationScheduleSeeder : IHostedService
{
    private const string SeedDisplayName = "Model recommendation refresh (on demand)";

    private const string SeedDescription =
        "Runs the local model advisor on demand to refresh the cached box-aware GGUF recommendation snapshot. " +
        "This is a manual, on-demand schedule (no automatic firing) — use Refresh now to run it.";

    private const string SeedTimeZoneId = "UTC";

    private const int SeedMaxRuntimeSeconds = 600;

    /// <summary>
    ///     The default parameter JSON for the seeded schedule: the Recommend operation, the coding use case and the top-5
    ///     limit. No approved-image or provider-name fields (the advisor runs box-aware GGUF recommendation in-process).
    ///     Mirrors the handler's own <c>DefaultParameters</c> so the seeded job runs the same recommendation as a
    ///     hand-created one.
    /// </summary>
    private const string SeedParametersJson = """{"operation":"Recommend","useCase":"coding","limit":5}""";

    private readonly ILogger<ModelRecommendationScheduleSeeder> _logger;

    private readonly IServiceScopeFactory _scopeFactory;

    public ModelRecommendationScheduleSeeder(IServiceScopeFactory scopeFactory,
        ILogger<ModelRecommendationScheduleSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var managementService = scope.ServiceProvider.GetRequiredService<IScheduledJobManagementService>();

            var jobs = await managementService.ListJobsAsync(false, cancellationToken).ConfigureAwait(false);
            if (jobs.Any(job => job.TemplateId == ModelRecommendationCheckHandler.TemplateIdValue))
            {
                // A definition for this template already exists — nothing to seed (idempotent).
                return;
            }

            var input = new ScheduledJobManagementInput(ModelRecommendationCheckHandler.TemplateIdValue,
                SeedDisplayName,
                SeedDescription,
                ScheduleKind.Manual,
                null,
                null,
                null,
                null,
                null,
                SeedTimeZoneId,
                SchedulerMisfirePolicy.SkipMissed,
                true,
                SeedMaxRuntimeSeconds,
                SeedParametersJson);

            // CreateJobAsync persists the definition enabled, then registers the durable Manual Quartz job (no trigger).
            var created = await managementService.CreateJobAsync(input, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Seeded on-demand {TemplateId} schedule {ScheduledJobId} (Manual, enabled).",
                ModelRecommendationCheckHandler.TemplateIdValue,
                created.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            // Seeding is best-effort: a node must start even if the seed fails. The operator can still hand-create the
            // schedule, and the next startup re-attempts once the underlying issue clears.
            _logger.LogWarning(ex,
                "Model recommendation schedule seeding failed at startup; the on-demand refresh job may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
