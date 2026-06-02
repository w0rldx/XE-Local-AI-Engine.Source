namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fake;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker 3 no-bypass proof: a real <see cref="SchedulerDispatchExecutor" /> dispatching a definition that
///     references the <c>model-recommendation-check</c> template runs the REAL handler through the registry's
///     <c>TryGetHandler</c>, opening a <c>scheduled_job_runs</c> row that goes Running → Succeeded AND creating a
///     <c>model_fit_snapshots</c> row. The refresh service is wired only behind the scheduler — there is no direct
///     execution entry point in this graph.
/// </summary>
public sealed class ModelRecommendationCheckSchedulerPathTests
{
    private const string ApprovedImageId = "llmfit-recommender-0-9-30";
    private const string ValidReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";
    private const string ParametersJson =
        """{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "coding", "limit": 5, "providerName": "ollama" }""";

    private static readonly Guid JobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);

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
        await runStore.Received(1).UpdateLifecycleAsync(
            RunId,
            ScheduledRunStatus.Succeeded,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // Snapshot row created by the refresh service and marked Succeeded with normalized rows.
        AssertEx.Equal(1, snapshotStore.Snapshots.Count);
        var snapshot = snapshotStore.Snapshots.Values.Single();
        AssertEx.Equal(ModelFitRunStatus.Succeeded, snapshot.Status);
        AssertEx.True(snapshot.IsLatestSuccessful, "the scheduler-path run must produce a latest-successful snapshot.");

        AssertEx.Equal(ModelRecommendationCheckHandler.TemplateIdValue, handler.TemplateId);
    }

    private static SchedulerDispatchExecutor BuildExecutor(
        InMemoryModelFitSnapshotStore snapshotStore,
        out IScheduledJobRunStore runStore,
        out ModelRecommendationCheckHandler handler)
    {
        var runner = new FakeModelFitUtilityRunner();
        runner.ScriptResult(new ModelFitUtilityRunResult(
            Status: ModelFitRunStatus.Succeeded,
            ExitCode: 0,
            StandardOutput: """{ "models": [ { "name": "qwen2.5-coder:7b", "score": 80, "fit_level": "Good", "run_mode": "GPU", "best_quant": "Q5_K_M", "estimated_tps": 40, "memory_required_gb": 6, "effective_context_length": 16384, "installed": true } ], "system": { "cpu_cores": 16 } }""",
            StandardError: string.Empty,
            Completed: true,
            DurationMs: 100,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            SanitizedError: null));

        var approvedImageStore = new InMemoryApprovedUtilityImageStore(Descriptor());
        var securityOptions = Options.Create(new SecurityOptions { AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$" });
        var refreshService = new ModelFitRefreshService(
            new ApprovedImageResolver(approvedImageStore, new ApprovedImageReferenceValidator()),
            new ModelFitRequestValidator(new ModelNameValidator(securityOptions)),
            new StubCapabilityReporter(),
            runner,
            snapshotStore,
            new InMemoryModelFitRecommendationStore(),
            approvedImageStore,
            TimeProvider.System,
            NullLogger<ModelFitRefreshService>.Instance);

        // The handler resolves the scoped refresh service + validator through a real scope factory (singleton handler).
        var services = new ServiceCollection();
        services.AddSingleton<IModelFitRefreshService>(refreshService);
        services.AddSingleton(securityOptions);
        services.AddSingleton<ModelNameValidator>();
        services.AddSingleton<ModelFitRequestValidator>();
        var provider = services.BuildServiceProvider();

        handler = new ModelRecommendationCheckHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModelRecommendationCheckHandler>.Instance);

        var definitionStore = Substitute.For<IScheduledJobDefinitionStore>();
        definitionStore.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ScheduledJobDefinitionRecord?>(DefinitionRecord()));

        runStore = Substitute.For<IScheduledJobRunStore>();
        runStore.UpsertByFireInstanceAsync(Arg.Any<ScheduledJobRunInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(RunRecord(ScheduledRunStatus.Running)));
        runStore.UpdateLifecycleAsync(
                    Arg.Any<Guid>(), Arg.Any<ScheduledRunStatus>(), Arg.Any<long?>(), Arg.Any<long?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult<ScheduledJobRunRecord?>(RunRecord((ScheduledRunStatus)callInfo[1])));

        var eventStore = Substitute.For<IScheduledJobRunEventStore>();
        eventStore.AddAsync(Arg.Any<ScheduledJobRunEventInput>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => Task.FromResult(EventRecord((ScheduledJobRunEventInput)callInfo[0])));

        var registry = new ScheduledJobTemplateRegistry([handler]);

        return new SchedulerDispatchExecutor(
            definitionStore,
            registry,
            runStore,
            eventStore,
            Substitute.For<ISchedulerEventPublisher>(),
            TimeProvider.System,
            NullLogger<SchedulerDispatchExecutor>.Instance);
    }

    private static ApprovedUtilityImageRecord Descriptor() =>
        new(
            ApprovedImageId: ApprovedImageId,
            DisplayName: "llmfit",
            Description: null,
            Purpose: UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
            ImageReference: ValidReference,
            SourceUrl: null,
            UpstreamVersion: "0.9.30",
            Enabled: true,
            DeprecatedAtUtc: null,
            ReplacementApprovedImageId: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LastUsedAtUtc: null,
            LastSuccessfulRunAtUtc: null,
            DiagnosticsJson: null);

    private static ScheduledJobDefinitionRecord DefinitionRecord() =>
        new(
            Id: JobId,
            TemplateId: ModelRecommendationCheckHandler.TemplateIdValue,
            DisplayName: "Model recommendation check",
            Description: null,
            Enabled: true,
            ScheduleKind: ScheduleKind.Cron,
            CronExpression: "0 0 * * * ?",
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.SkipMissed,
            PreventOverlap: false,
            MaxRuntimeSeconds: 600,
            ParameterJson: ParametersJson,
            CreatedBy: ScheduledJobCreator.User,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            DeletedAtUtc: null);

    private static ScheduledJobRunRecord RunRecord(ScheduledRunStatus status) =>
        new(
            Id: RunId,
            ScheduledJobId: JobId,
            TemplateId: ModelRecommendationCheckHandler.TemplateIdValue,
            QuartzFireInstanceId: "fire-modelfit",
            TriggeredBy: ScheduledRunTrigger.Schedule,
            Status: status,
            ScheduledFireTimeUtc: null,
            ActualFireTimeUtc: Now.ToUnixTimeMilliseconds(),
            CompletedAtUtc: null,
            DurationMs: null,
            Summary: null,
            DetailsJson: null,
            ErrorMessage: null,
            ErrorDetails: null,
            CancellationRequestedAtUtc: null,
            CreatedAtUtc: 1L);

    private static ScheduledJobRunEventRecord EventRecord(ScheduledJobRunEventInput input) =>
        new(Guid.NewGuid(), input.RunId, input.Sequence, input.Level, input.Message, input.DataJson, 1L);

    private sealed class StubCapabilityReporter : ICapabilityReporter
    {
        public Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClientCapabilities { RamMb = 32_768, VramMb = 8_192, CudaAvailable = true });

        public Task ReportToApiAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
