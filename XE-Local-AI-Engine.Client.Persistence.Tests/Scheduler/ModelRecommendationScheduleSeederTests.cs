namespace XE_Local_AI_Engine.Client.Persistence.Tests.Scheduler;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

/// <summary>
///     Integration tests for <see cref="ModelRecommendationScheduleSeeder" />. Uses a fully-migrated temporary SQLite
///     database + the real Quartz ADO.NET store and the real <see cref="ModelRecommendationCheckHandler" /> template so
///     the seeder's create/list path runs against the production registration.
/// </summary>
public sealed class ModelRecommendationScheduleSeederTests : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-sched-seed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task StartAsync_WhenNoDefinitionExists_SeedsOneEnabledManualModelRecommendationJob()
    {
        var dbPath = GetDatabasePath("seed-fresh.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildProvider(dbPath);
        var seeder = ActivatorUtilities.CreateInstance<ModelRecommendationScheduleSeeder>(provider);

        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var jobs = await service.ListJobsAsync().ConfigureAwait(false);
        var seeded = jobs.Where(j => j.TemplateId == ModelRecommendationCheckHandler.TemplateIdValue).ToList();

        AssertEx.Equal(expected: 1, seeded.Count, "Exactly one model-recommendation-check definition must be seeded.");
        AssertEx.Equal(ScheduleKind.Manual, seeded[0].ScheduleKind, "The seeded definition must be a Manual job.");
        AssertEx.Equal(expected: true, seeded[0].Enabled, "The seeded definition must be enabled by default.");

        // The durable Manual Quartz job must be registered (so TriggerNowAsync can fire it) with no trigger.
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        var jobKey = new JobKey(seeded[0].Id.ToString("N"), SchedulerJobKeys.Group);
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "The seeded Manual job must be registered as a durable Quartz job.");
        var triggers = await scheduler.GetTriggersOfJob(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, triggers.Count, "The seeded Manual job must have no trigger.");
    }

    [Test]
    public async Task StartAsync_WhenRunTwice_DoesNotCreateADuplicate()
    {
        var dbPath = GetDatabasePath("seed-idempotent.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildProvider(dbPath);
        var seeder = ActivatorUtilities.CreateInstance<ModelRecommendationScheduleSeeder>(provider);

        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await seeder.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var jobs = await service.ListJobsAsync().ConfigureAwait(false);
        var seeded = jobs.Count(j => j.TemplateId == ModelRecommendationCheckHandler.TemplateIdValue);

        AssertEx.Equal(expected: 1, seeded, "Re-running the seeder must not create a duplicate definition.");
    }

    [Test]
    public async Task StartAsync_WhenManagementServiceFails_SwallowsAndDoesNotThrow()
    {
        // A provider WITHOUT the scheduler registered cannot resolve IScheduledJobManagementService, so the seeder's
        // GetRequiredService throws InvalidOperationException — which must be caught and swallowed so the node still
        // starts. This proves the best-effort guard.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        await using var provider = services.BuildServiceProvider();

        var seeder = ActivatorUtilities.CreateInstance<ModelRecommendationScheduleSeeder>(provider);

        // Pin the precondition: the dependency really is unresolvable, so this exercises the swallow path rather
        // than passing because the seeder silently found nothing to do.
        _ = AssertEx.Throws<InvalidOperationException>(() => provider.GetRequiredService<IScheduledJobManagementService>(),
            "The provider must not be able to resolve IScheduledJobManagementService, or the guard is never reached.");

        var start = seeder.StartAsync(CancellationToken.None);
        await start.ConfigureAwait(false);

        AssertEx.True(start.IsCompletedSuccessfully,
            "StartAsync must swallow the resolution failure so the node still starts.");
    }


    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private async Task MigrateAsync(string dbPath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(dbPath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static ServiceProvider BuildProvider(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Real stores backed by the migrated SQLite DB with encryption interceptors.
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((sp, options) =>
        {
            options.UseSqlite($"Data Source={dbPath}")
                   .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                   .AddInterceptors(sp.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       sp.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();
        services.AddSingleton(TimeProvider.System);

        // ScheduledJobManagementService reads the node "Maximum message request timeout" to derive the run-agent
        // template's implicit Quartz ceiling; AddNodeScheduler does not own that store, so the test supplies it.
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        nodeSettingsStore.Load(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        services.AddSingleton(nodeSettingsStore);

        var config = BuildConfig($"Data Source={dbPath}");
        services.AddSingleton<IConfiguration>(config);

        // AddNodeScheduler registers the real ModelRecommendationCheckHandler template, the scheduler factory, and the
        // management service the seeder drives.
        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfig(string connectionString)
    {
        return new ConfigurationBuilder()
               .AddInMemoryCollection(new Dictionary<string, string?>
               {
                   ["Scheduler:Enabled"] = "true",
                   ["Scheduler:MaxConcurrency"] = "2",
                   ["Scheduler:DefaultMaxRuntimeMinutes"] = "5",
                   ["Scheduler:QuartzTablePrefix"] = "QRTZ_",
                   ["ConnectionStrings:node-sqlite"] = connectionString
               })
               .Build();
    }

    /// <summary>
    ///     Minimal <see cref="IHostApplicationBuilder" /> shim mirroring the one in
    ///     <see cref="ScheduledJobManagementServiceTests" />.
    /// </summary>
    private sealed class MinimalHostApplicationBuilder(IServiceCollection services) : IHostApplicationBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfigurationManager Configuration { get; } = new ConfigurationManager();
        public IHostEnvironment Environment { get; } = Substitute.For<IHostEnvironment>();
        public ILoggingBuilder Logging { get; } = Substitute.For<ILoggingBuilder>();
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();
        public IMetricsBuilder Metrics { get; } = Substitute.For<IMetricsBuilder>();

        public void ConfigureContainer<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory,
            Action<TContainerBuilder>? configure = null) where TContainerBuilder : notnull
        {
        }
    }
}
