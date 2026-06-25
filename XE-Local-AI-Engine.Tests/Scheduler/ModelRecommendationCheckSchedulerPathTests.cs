namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     No-bypass proof: a real <see cref="SchedulerDispatchExecutor" /> dispatching a definition that
///     references the <c>model-recommendation-check</c> template runs the REAL handler through the registry's
///     <c>TryGetHandler</c>, opening a <c>scheduled_job_runs</c> row that goes Running → Succeeded AND creating a
///     <c>model_fit_snapshots</c> row via the local model advisor. The advisor is wired only behind the scheduler —
///     there is no direct execution entry point in this graph. The new parameter schema carries no approved-image /
///     provider-name fields.
/// </summary>
public sealed class ModelRecommendationCheckSchedulerPathTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private const string ParametersJson =
        """{ "operation": "Recommend", "useCase": "coding", "limit": 5 }""";

    private static readonly Guid JobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(year: 2026, month: 6, day: 2, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public async Task DispatchAsync_ThroughSchedulerPath_RecordsSucceededRunAndCreatesSnapshot()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var executor = BuildExecutor(snapshotStore, out var runStore, out var handler);

        await executor.DispatchAsync(JobId, "fire-modelfit", Now, Now, CancellationToken.None);

        // Run row: Running opened, then Succeeded via the dispatcher (handler never touches run rows).
        await runStore.Received(1).UpsertByFireInstanceAsync(
            Arg.Is<ScheduledJobRunInput>(i => i.Status == ScheduledRunStatus.Running && i.TemplateId == ModelRecommendationCheckHandler.TemplateIdValue),
            Arg.Any<CancellationToken>());
        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Succeeded,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Snapshot row created by the advisor and marked Succeeded with normalized rows.
        AssertEx.Equal(expected: 1, snapshotStore.Snapshots.Count);
        var snapshot = snapshotStore.Snapshots.Values.Single();
        AssertEx.Equal(ModelFitRunStatus.Succeeded, snapshot.Status);
        AssertEx.True(snapshot.IsLatestSuccessful, "the scheduler-path run must produce a latest-successful snapshot.");

        AssertEx.Equal(ModelRecommendationCheckHandler.TemplateIdValue, handler.TemplateId);
    }

    private static SchedulerDispatchExecutor BuildExecutor(InMemoryModelFitSnapshotStore snapshotStore,
        out IScheduledJobRunStore runStore,
        out ModelRecommendationCheckHandler handler)
    {
        var refreshService = BuildAdvisor(snapshotStore);

        // The handler resolves the scoped advisor + validator through a real scope factory (singleton handler).
        var securityOptions = Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:/-]+$"
        });
        var services = new ServiceCollection();
        services.AddSingleton<IModelFitRefreshService>(refreshService);
        services.AddSingleton(securityOptions);
        services.AddSingleton<ModelNameValidator>();
        services.AddSingleton<ModelFitRequestValidator>();
        var provider = services.BuildServiceProvider();

        handler = new ModelRecommendationCheckHandler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModelRecommendationCheckHandler>.Instance);

        var definitionStore = Substitute.For<IScheduledJobDefinitionStore>();
        definitionStore.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ScheduledJobDefinitionRecord?>(DefinitionRecord()));

        runStore = Substitute.For<IScheduledJobRunStore>();
        runStore.UpsertByFireInstanceAsync(Arg.Any<ScheduledJobRunInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(RunRecord(ScheduledRunStatus.Running)));
        runStore.UpdateLifecycleAsync(Arg.Any<Guid>(), Arg.Any<ScheduledRunStatus>(), Arg.Any<long?>(), Arg.Any<long?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult<ScheduledJobRunRecord?>(RunRecord((ScheduledRunStatus)callInfo[1])));

        var eventStore = Substitute.For<IScheduledJobRunEventStore>();
        eventStore.AddAsync(Arg.Any<ScheduledJobRunEventInput>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => Task.FromResult(EventRecord((ScheduledJobRunEventInput)callInfo[0])));

        var registry = new ScheduledJobTemplateRegistry([handler]);

        return new SchedulerDispatchExecutor(definitionStore,
            registry,
            runStore,
            eventStore,
            Substitute.For<ISchedulerEventPublisher>(),
            TimeProvider.System,
            NullLogger<SchedulerDispatchExecutor>.Instance);
    }

    private static ModelFitRefreshService BuildAdvisor(InMemoryModelFitSnapshotStore snapshotStore)
    {
        var profile = new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 24 * Gb,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(profile));

        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     new GgufRepoSummary("org/qwen-GGUF", IsGated: false, Downloads: 1000, Likes: 10, DateTimeOffset.UnixEpoch, "apache-2.0", HasUsableGguf: true)
                 ]));
        discovery.InspectRepoAsync("org/qwen-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new GgufRepoDetail("org/qwen-GGUF", IsGated: false, "apache-2.0",
                 [
                     new GgufRepoFile("qwen.Q4_K_M.gguf", "Q4_K_M", 4 * Gb, Sha256: null, "main",
                         "qwen2", "Q4_K_M", ParamCount: 7_000_000_000L, BlockCount: 28, AttentionHeadCount: 28, AttentionHeadCountKV: 4, EmbeddingLength: 3584, ContextLength: 32768)
                 ])));

        var registry = Substitute.For<IGgufModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<GgufModelRegistryEntry>>([]));

        var securityOptions = Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:/-]+$"
        });

        return new ModelFitRefreshService(profiler,
            discovery,
            new MemoryFitEstimator(),
            Substitute.For<IGgufModelStore>(),
            registry,
            Substitute.For<ILlamaServerProcessSupervisor>(),
            new ModelFitRequestValidator(new ModelNameValidator(securityOptions)),
            snapshotStore,
            new InMemoryModelFitRecommendationStore(),
            TimeProvider.System,
            NullLogger<ModelFitRefreshService>.Instance);
    }

    private static ScheduledJobDefinitionRecord DefinitionRecord()
    {
        return new ScheduledJobDefinitionRecord(JobId,
            ModelRecommendationCheckHandler.TemplateIdValue,
            "Model recommendation check",
            Description: null,
            Enabled: true,
            ScheduleKind.Cron,
            "0 0 * * * ?",
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.SkipMissed,
            PreventOverlap: false,
            MaxRuntimeSeconds: 600,
            ParametersJson,
            ScheduledJobCreator.User,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            DeletedAtUtc: null);
    }

    private static ScheduledJobRunRecord RunRecord(ScheduledRunStatus status)
    {
        return new ScheduledJobRunRecord(RunId,
            JobId,
            ModelRecommendationCheckHandler.TemplateIdValue,
            "fire-modelfit",
            ScheduledRunTrigger.Schedule,
            status,
            ScheduledFireTimeUtc: null,
            Now.ToUnixTimeMilliseconds(),
            CompletedAtUtc: null,
            DurationMs: null,
            Summary: null,
            DetailsJson: null,
            ErrorMessage: null,
            ErrorDetails: null,
            CancellationRequestedAtUtc: null,
            CreatedAtUtc: 1L);
    }

    private static ScheduledJobRunEventRecord EventRecord(ScheduledJobRunEventInput input)
    {
        return new ScheduledJobRunEventRecord(Guid.NewGuid(), input.RunId, input.Sequence, input.Level, input.Message, input.DataJson, OccurredAtUtc: 1L);
    }
}
