namespace XE_Local_AI_Engine.Client.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

/// <summary>
///     Registers the node-local Quartz scheduler runtime: the persistent (SQLite) job store, the hosted service that
///     drives it, the dispatch executor + dispatch <see cref="IJob" /> variants, and the template registry. The QRTZ_
///     tables are created by the scheduler EF migration in the same node-chat SQLite database, so the store runs with
///     schema validation on. When <see cref="SchedulerOptions.Enabled" /> is <c>false</c> this registers nothing — the
///     persistence tables and options remain, but no scheduler or hosted service is wired up.
/// </summary>
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
                    // Resolve the shared node-sqlite connection by NAME, not value: Quartz looks the name up in
                    // IConfiguration's ConnectionStrings when the scheduler starts (post-config-build), exactly like the
                    // NodeChatDbContext registration reads it lazily. Reading the literal string here at registration
                    // time would throw under WebApplicationFactory, whose connection string is layered in after services
                    // are registered.
                    //
                    // Concurrency posture: the scheduler deliberately shares the node.sqlite file with chat/KB
                    // rather than owning its own DB. WAL is a persistent, file-level property that NodeSqlitePragmas sets
                    // once (via the EF connection interceptor during the startup migration, before this hosted service
                    // begins firing jobs), so Quartz's connections run under WAL automatically — its frequent polling reads
                    // never block, and never get blocked by, a concurrent chat/KB writer, which is the dominant contention
                    // pattern here. On the write side (infrequent job-store updates) Microsoft.Data.Sqlite's command-level
                    // busy retry plus the shared connection pool (Quartz reuses the same connection string, hence the same
                    // pool whose handles already carry busy_timeout) provide the contention resilience. Quartz 3.18's fluent
                    // config exposes no per-connection PRAGMA hook; the escape hatch, if evidence of scheduler write
                    // starvation ever appears, is a custom IDbProvider via db.UseConnectionProvider<T>().
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

        // Model-fit template handler. Registered as a Singleton because the registry captures every handler in
        // a FrozenDictionary at construction; the handler resolves the Scoped IModelFitRefreshService through an
        // IServiceScopeFactory scope per fire.
        builder.Services.AddSingleton<IScheduledJobHandler, ModelRecommendationCheckHandler>();

        // Run-a-saved-agent template handler. Also a Singleton (registry snapshot); it resolves its scoped
        // collaborators (agent resolver, capacity gate, runtime-package builder, invocation runner) per fire through an
        // IServiceScopeFactory scope and runs a node-local agent headlessly on a schedule.
        builder.Services.AddSingleton<IScheduledJobHandler, RunSavedAgentHandler>();

        // Default no-op publisher so the dispatcher/management service resolve a publisher in Application-only and test
        // hosts. The Client host registers a hub-backed publisher (ConfigureServices) that supersedes this.
        builder.Services.TryAddSingleton<ISchedulerEventPublisher, NullSchedulerEventPublisher>();

        return builder;
    }
}
