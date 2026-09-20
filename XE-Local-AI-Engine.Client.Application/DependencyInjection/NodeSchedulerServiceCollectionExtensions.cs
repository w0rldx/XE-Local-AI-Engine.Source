namespace XE_Local_AI_Engine.Client.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

/// <summary>
///     Registers the node-local Quartz scheduler runtime: the persistent SQLite job store, the hosted service that
///     drives it, the dispatch executor + dispatch <see cref="IJob" /> variants, and the template registry.
/// </summary>
/// <remarks>
///     When <see cref="SchedulerOptions.Enabled" /> is <c>false</c> this registers nothing: the persistence tables and
///     options remain, but no scheduler or hosted service is wired up. The QRTZ_ tables are created by the scheduler EF
///     migration in the same node-chat SQLite database, so the store runs with schema validation on.
///     See docs/wiki/06-scheduler.md ("DI registration &amp; two key gotchas").
/// </remarks>
public static class NodeSchedulerServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddNodeScheduler(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(SchedulerOptions.Section).Get<SchedulerOptions>() ?? new SchedulerOptions();
        if (!options.Enabled)
        {
            return builder;
        }

        builder.Services.AddQuartz(q =>
        {
            q.SchedulerName = "XE Local AI Engine Scheduler";
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = options.MaxConcurrency);
            q.UsePersistentStore(s =>
            {
                s.UseProperties = true;
                s.PerformSchemaValidation = true; // QRTZ_ tables are created by the scheduler EF migration.
                s.UseMicrosoftSQLite(db =>
                {
                    // Use the node-sqlite connection by NAME: Quartz resolves it from IConfiguration at scheduler start, and
                    // reading the literal value here throws under WebApplicationFactory. Posture: docs/wiki/06-scheduler.md.
                    db.ConnectionStringName = "node-sqlite";
                    db.TablePrefix = options.QuartzTablePrefix; // "QRTZ_"
                });
                s.UseSystemTextJsonSerializer();
            });
            q.UseTimeZoneConverter();
            q.UseJobAutoInterrupt(o => o.DefaultMaxRunTime = TimeSpan.FromMinutes(options.DefaultMaxRuntimeMinutes));
        });
        builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

        builder.Services.AddScoped<ISchedulerDispatchExecutor, SchedulerDispatchExecutor>();
        builder.Services.AddTransient<SchedulerDispatchJob>();
        builder.Services.AddTransient<NonOverlappingSchedulerDispatchJob>();
        builder.Services.AddSingleton<IScheduledJobTemplateRegistry, ScheduledJobTemplateRegistry>();
        builder.Services.AddScoped<IScheduledJobManagementService, ScheduledJobManagementService>();

        // Every template handler is a Singleton, because the registry captures them in a FrozenDictionary at construction, and each
        // resolves its Scoped collaborators through an IServiceScopeFactory scope per fire.
        builder.Services.AddSingleton<IScheduledJobHandler, ModelRecommendationCheckHandler>();

        // Resolves the agent resolver, capacity gate, runtime-package builder and invocation runner per fire, then runs a
        // node-local agent headlessly on a schedule.
        builder.Services.AddSingleton<IScheduledJobHandler, RunSavedAgentHandler>();

        // Resolves the benchmark store and freeze service per fire and only ENQUEUES the matrix, so the fire never holds
        // the scheduler thread for the runs' duration — the existing single-consumer benchmark queue drains it.
        builder.Services.AddSingleton<IScheduledJobHandler, RunBenchmarkBatchHandler>();

        // Default no-op publisher so the dispatcher/management service resolve a publisher in Application-only and test
        // hosts. The Client host registers a hub-backed publisher (ConfigureServices) that supersedes this.
        builder.Services.TryAddSingleton<ISchedulerEventPublisher, NullSchedulerEventPublisher>();

        // Ages out scheduled_job_runs rows on the SchedulerOptions cadence, resolving the run store from a per-sweep
        // scope; it registers here rather than in the host because only this layer may reach a persistence store.
        builder.Services.AddHostedService<SchedulerHistoryRetentionService>();

        // Startup self-heal for persisted Quartz job details whose stored JOB_CLASS_NAME no longer resolves. It must stay
        // last: only starting after AddQuartzHostedService leaves the scheduler factory and job store usable in StartAsync.
        builder.Services.AddHostedService<SchedulerJobDetailReconciliationService>();

        return builder;
    }
}
