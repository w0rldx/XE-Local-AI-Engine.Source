namespace XE_Local_AI_Engine.Client.Persistence.Tests.Scheduler;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Integration tests for <see cref="NodeSchedulerServiceCollectionExtensions.AddNodeScheduler" />.
///     Verifies DI resolution and Quartz startup against a fully-migrated temporary SQLite database
///     (QRTZ_ tables present via the scheduler migration).
/// </summary>
public sealed class NodeSchedulerRegistrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "xe-scheduler-reg-" + Guid.NewGuid().ToString("N"));

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }

        _keyHolder.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Enabled=false → no Quartz services registered
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void AddNodeScheduler_WhenDisabled_DoesNotRegisterISchedulerFactory()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(enabled: false, connectionString: "Data Source=:memory:");

        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        var provider = services.BuildServiceProvider();
        var schedulerFactory = provider.GetService<ISchedulerFactory>();

        AssertEx.Null(schedulerFactory,
            "No Quartz services should be registered when Scheduler:Enabled=false.");
    }

    [Test]
    public void AddNodeScheduler_WhenDisabled_DoesNotRegisterSchedulerApplicationServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(enabled: false, connectionString: "Data Source=:memory:");

        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        var provider = services.BuildServiceProvider();

        AssertEx.Null(provider.GetService<IScheduledJobTemplateRegistry>(),
            "IScheduledJobTemplateRegistry must not be registered when disabled.");
        AssertEx.Null(provider.GetService<ISchedulerDispatchExecutor>(),
            "ISchedulerDispatchExecutor must not be registered when disabled.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Enabled=true → ISchedulerFactory resolves; scheduler starts without
    // schema-validation error (PerformSchemaValidation=true in production config)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AddNodeScheduler_WhenEnabled_ISchedulerFactoryResolvesAndSchedulerStartsClean()
    {
        var dbPath = GetDatabasePath("scheduler-reg-factory.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);

        var factory = provider.GetService<ISchedulerFactory>();
        AssertEx.NotNull(factory, "ISchedulerFactory must be registered when Scheduler:Enabled=true.");

        // GetScheduler triggers the Quartz ADO store to connect and validate the QRTZ_ schema.
        // PerformSchemaValidation=true means any missing table surfaces here as an exception.
        var scheduler = await factory!.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        AssertEx.NotNull(scheduler, "IScheduler must be obtainable from the factory.");

        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(scheduler.IsStarted, "Scheduler must report IsStarted after Start().");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task AddNodeScheduler_WhenEnabled_ApplicationServicesResolve()
    {
        var dbPath = GetDatabasePath("scheduler-reg-services.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);

        // Registry is singleton.
        var registry = provider.GetService<IScheduledJobTemplateRegistry>();
        AssertEx.NotNull(registry, "IScheduledJobTemplateRegistry must resolve.");

        // Executor is scoped — resolve within a scope.
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetService<ISchedulerDispatchExecutor>();
        AssertEx.NotNull(executor, "ISchedulerDispatchExecutor must resolve as scoped.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Durable survive-restart: schedule a job+trigger, dispose, recreate from
    // the same sqlite file, assert job and trigger persisted.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AddNodeScheduler_JobAndTrigger_SurviveSchedulerRestart()
    {
        var dbPath = GetDatabasePath("scheduler-persist-restart.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        var jobKey = new JobKey("persist-test", SchedulerJobKeys.Group);
        var jobId = Guid.NewGuid();

        // ── First scheduler instance: schedule job + trigger, then shut down ──
        await using (var provider1 = BuildEnabledProvider(dbPath))
        {
            var factory1 = provider1.GetRequiredService<ISchedulerFactory>();
            var sched1 = await factory1.GetScheduler(CancellationToken.None).ConfigureAwait(false);
            await sched1.Start(CancellationToken.None).ConfigureAwait(false);

            var job = JobBuilder.Create<NoOpTestJob>()
                .WithIdentity(jobKey)
                .UsingJobData(SchedulerJobKeys.ScheduledJobIdKey, jobId.ToString())
                .StoreDurably()
                .Build();

            // Far-future trigger — will never fire during the test.
            var trigger = TriggerBuilder.Create()
                .WithIdentity("persist-trigger", SchedulerJobKeys.Group)
                .ForJob(jobKey)
                .StartAt(DateTimeOffset.UtcNow.AddYears(10))
                .Build();

            await sched1.ScheduleJob(job, trigger, CancellationToken.None).ConfigureAwait(false);
            await sched1.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
        }

        // ── Second scheduler instance: open same DB, assert persistence ──
        await using (var provider2 = BuildEnabledProvider(dbPath))
        {
            var factory2 = provider2.GetRequiredService<ISchedulerFactory>();
            var sched2 = await factory2.GetScheduler(CancellationToken.None).ConfigureAwait(false);
            await sched2.Start(CancellationToken.None).ConfigureAwait(false);

            var jobDetail = await sched2.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false);
            AssertEx.NotNull(jobDetail, "Job must persist across scheduler restart.");
            AssertEx.Equal(
                jobId.ToString(),
                jobDetail!.JobDataMap.GetString(SchedulerJobKeys.ScheduledJobIdKey),
                "JobDataMap scheduledJobId must survive restart.");

            var triggers = await sched2.GetTriggersOfJob(jobKey, CancellationToken.None).ConfigureAwait(false);
            AssertEx.True(triggers.Count > 0, "At least one trigger must persist across restart.");

            await sched2.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildEnabledProvider(string dbPath)
    {
        var services = new ServiceCollection();
        // Use NullLoggerFactory (static singleton) so Quartz's static LogProvider does not
        // cache a reference to a disposable LoggerFactory that gets torn down between the
        // two scheduler instances in the survive-restart test.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IScheduledJobHandler, TestEchoScheduledJobHandler>();

        // The stores + TimeProvider are required by SchedulerDispatchExecutor (scoped). They live in
        // AddNodeApplication in the real host; this hand-rolled provider only calls AddNodeScheduler, so supply
        // substitutes here so the executor's dependency graph resolves.
        services.AddScoped<IScheduledJobDefinitionStore>(_ => Substitute.For<IScheduledJobDefinitionStore>());
        services.AddScoped<IScheduledJobRunStore>(_ => Substitute.For<IScheduledJobRunStore>());
        services.AddScoped<IScheduledJobRunEventStore>(_ => Substitute.For<IScheduledJobRunEventStore>());
        services.AddSingleton(TimeProvider.System);

        var config = BuildConfig(enabled: true, connectionString: $"Data Source={dbPath}");
        // Quartz resolves the SQLite data source by connection-string NAME ("node-sqlite") from the
        // IConfiguration in the DI container at scheduler-start time (see NodeSchedulerServiceCollectionExtensions:
        // db.ConnectionStringName). The real application host always has IConfiguration registered, so this
        // hand-rolled provider must register it too for the named lookup to succeed.
        services.AddSingleton<IConfiguration>(config);
        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        return services.BuildServiceProvider();
    }

    private async Task MigrateAsync(string dbPath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(dbPath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static IConfiguration BuildConfig(bool enabled, string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Enabled"] = enabled ? "true" : "false",
                ["Scheduler:MaxConcurrency"] = "2",
                ["Scheduler:DefaultMaxRuntimeMinutes"] = "5",
                ["Scheduler:QuartzTablePrefix"] = "QRTZ_",
                ["ConnectionStrings:node-sqlite"] = connectionString
            })
            .Build();

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    /// <summary>
    ///     Minimal Quartz <see cref="IJob" /> used in the survive-restart test so we avoid referencing the
    ///     <c>internal</c> production job types (<c>SchedulerDispatchJob</c>) from this test assembly.
    ///     The test only needs to verify that Quartz persists a job+trigger across a scheduler restart;
    ///     the specific job type is irrelevant to that assertion.
    /// </summary>
    private sealed class NoOpTestJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    /// <summary>
    ///     Minimal <see cref="IHostApplicationBuilder" /> shim that satisfies the extension method's
    ///     <c>builder.Services</c> and <c>builder.Configuration</c> needs without spinning up a full host.
    /// </summary>
    private sealed class MinimalHostApplicationBuilder(IServiceCollection services) : IHostApplicationBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfigurationManager Configuration { get; } = new ConfigurationManager();
        public IHostEnvironment Environment { get; } = Substitute.For<IHostEnvironment>();
        public ILoggingBuilder Logging { get; } = Substitute.For<ILoggingBuilder>();
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();
        public IMetricsBuilder Metrics { get; } = Substitute.For<IMetricsBuilder>();

        public void ConfigureContainer<TContainerBuilder>(
            IServiceProviderFactory<TContainerBuilder> factory,
            Action<TContainerBuilder>? configure = null) where TContainerBuilder : notnull { }
    }
}
