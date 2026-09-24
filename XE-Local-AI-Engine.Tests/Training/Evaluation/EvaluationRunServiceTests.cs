namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Evaluation creation always borrows the training run's immutable membership. A later live review edit must not
///     prevent a base or tuned evaluation from replaying the exact corpus that run trained against.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EvaluationRunServiceTests
{
    private static readonly Guid DatasetId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid HoldoutSampleId = Guid.NewGuid();
    private static readonly string FrozenFingerprint = "v1:" + new string('a', count: 64);

    [Test]
    public async Task Create_WhenTheLiveDatasetChangedSinceTheFreeze_StillEnqueuesTheFrozenMembership()
    {
        var harness = Harness.Create(datasetFingerprint: "v1:" + new string('b', count: 64));

        _ = await harness.Service.CreateAsync(new CreateEvaluationCommand
        {
            TrainingRunId = RunId,
            Target = EvaluationTarget.Base
        });

        var enqueued = AssertEx.NotNull(harness.Enqueued);
        AssertEx.Equal(FrozenFingerprint, enqueued.DatasetContentFingerprint);
        var membership = JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(enqueued.MembershipJson.Span, TrainingJson.Options)!;
        AssertEx.Equal(HoldoutSampleId, membership.HoldoutSampleIds.Single());
    }

    [Test]
    public async Task Create_WhenTheDatasetStillMatchesTheFreeze_EnqueuesTheFrozenMembership()
    {
        var harness = Harness.Create(datasetFingerprint: FrozenFingerprint);

        _ = await harness.Service.CreateAsync(new CreateEvaluationCommand
        {
            TrainingRunId = RunId,
            Target = EvaluationTarget.Base
        });

        var enqueued = AssertEx.NotNull(harness.Enqueued, "A matching fingerprint must reach the store.");
        AssertEx.Equal(FrozenFingerprint, enqueued.DatasetContentFingerprint);
        var membership = AssertEx.NotNull(JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(enqueued.MembershipJson.Span, TrainingJson.Options),
            "The membership must round-trip.");
        AssertEx.Equal(HoldoutSampleId, membership.HoldoutSampleIds.Single());
    }

    [Test]
    public async Task Create_WhenTargetIsUndefined_IsRejectedBeforeEnqueue()
    {
        var harness = Harness.Create(datasetFingerprint: FrozenFingerprint);

        var exception = await AssertEx.ThrowsAsync<EvaluationRejectedException>(() =>
            harness.Service.CreateAsync(new CreateEvaluationCommand
            {
                TrainingRunId = RunId,
                Target = EvaluationTarget.Undefined
            }));

        AssertEx.Contains(exception.Message, "target is required", StringComparison.OrdinalIgnoreCase);
        _ = await harness.Evaluations.DidNotReceiveWithAnyArgs()
                         .CreateAndEnqueueAsync(default!, CancellationToken.None);
    }

    /// <summary>
    ///     The three ways a cancel can land, which is what the operator surface offers and refuses the control on. A
    ///     QUEUED evaluation is terminalized by the request itself; a RUNNING one is only signalled, because the
    ///     executor owns the terminal write and cancelling from two places would race the work item; anything already
    ///     terminal answers false, which the endpoint turns into the 404 that keeps the control off a finished row.
    /// </summary>
    [Test]
    public async Task Cancel_TerminalizesAQueuedEvaluation_SignalsARunningOne_AndRefusesATerminalOne()
    {
        var harness = Harness.Create(datasetFingerprint: FrozenFingerprint);
        var queuedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var doneId = Guid.NewGuid();
        harness.Existing(queuedId, TrainingEvaluationStatus.Queued);
        harness.Existing(runningId, TrainingEvaluationStatus.Running);
        harness.Existing(doneId, TrainingEvaluationStatus.Succeeded);

        AssertEx.True(await harness.Service.CancelAsync(queuedId), "A queued evaluation is cancellable.");
        _ = await harness.Evaluations.Received(1)
                         .CompleteAsync(queuedId, TrainingWorkStatus.Cancelled, Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var running = new CancellationTokenSource();
        using (harness.Cancellations.Register(runningId, running))
        {
            AssertEx.True(await harness.Service.CancelAsync(runningId), "A running evaluation is cancellable.");
        }

        AssertEx.True(running.IsCancellationRequested, "A running evaluation is signalled through the registry.");
        _ = await harness.Evaluations.DidNotReceive()
                         .CompleteAsync(runningId, Arg.Any<TrainingWorkStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        AssertEx.False(await harness.Service.CancelAsync(doneId), "A finished evaluation has nothing to cancel.");
        AssertEx.False(await harness.Service.CancelAsync(Guid.NewGuid()), "An unknown evaluation has nothing to cancel.");
    }

    /// <summary>One service over substituted stores; only the irrelevant live dataset fingerprint varies.</summary>
    private sealed class Harness
    {
        private Harness(EvaluationRunService service, ITrainingEvaluationStore evaluations, TrainingRunCancellationRegistry cancellations)
        {
            Service = service;
            Evaluations = evaluations;
            Cancellations = cancellations;
        }

        public EvaluationRunService Service { get; }

        public ITrainingEvaluationStore Evaluations { get; }

        public TrainingRunCancellationRegistry Cancellations { get; }

        /// <summary>Makes the store answer with one evaluation in <paramref name="status" /> for that id.</summary>
        public void Existing(Guid evaluationId, TrainingEvaluationStatus status) =>
            _ = Evaluations.GetAsync(evaluationId, Arg.Any<CancellationToken>())
                           .Returns(Evaluation() with
                           {
                               Id = evaluationId,
                               Status = status
                           });

        public TrainingEvaluationEnqueueCommand? Enqueued { get; private set; }

        public static Harness Create(string datasetFingerprint)
        {
            var freeze = new TrainingRunFreezeV1
            {
                FreezeId = Guid.NewGuid(),
                DatasetContentFingerprint = FrozenFingerprint,
                DatasetRevision = 1,
                HoldoutSampleIds = [HoldoutSampleId]
            };

            var runs = Substitute.For<ITrainingRunStore>();
            _ = runs.GetAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run(freeze));

            var datasets = Substitute.For<ITrainingDatasetStore>();
            _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(datasetFingerprint));

            var models = Substitute.For<IGgufModelStore>();
            _ = models.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                      .Returns<IReadOnlyList<LocalModelDescriptor>>([
                          new LocalModelDescriptor
                          {
                              ModelName = "base:Q4_K_M",
                              ProviderName = "llamacpp",
                              IsAvailable = true,
                              SizeBytes = 1,
                              ModifiedAt = null,
                              MaxContextTokens = null,
                              ModelContentFingerprint = "v1:def"
                          }
                      ]);

            var evaluations = Substitute.For<ITrainingEvaluationStore>();
            var cancellations = new TrainingRunCancellationRegistry();
            var harness = new Harness(new EvaluationRunService(evaluations, runs, datasets, models,
                cancellations, Substitute.For<ITrainingRunQueueSignal>()), evaluations, cancellations);
            _ = evaluations.CreateAndEnqueueAsync(Arg.Any<TrainingEvaluationEnqueueCommand>(), Arg.Any<CancellationToken>())
                           .Returns(callInfo =>
                           {
                               harness.Enqueued = callInfo.Arg<TrainingEvaluationEnqueueCommand>();
                               return Task.FromResult(Evaluation());
                           });
            return harness;
        }

        private static TrainingRunRecord Run(TrainingRunFreezeV1 freeze) =>
            new()
            {
                Id = RunId,
                DatasetId = DatasetId,
                DatasetContentFingerprint = FrozenFingerprint,
                DatasetRevision = 1,
                FreezeJson = JsonSerializer.SerializeToUtf8Bytes(freeze, TrainingJson.Options),
                BaseArtifactId = Guid.NewGuid(),
                LinkedInstalledModelName = "base:Q4_K_M",
                LinkedModelContentFingerprint = "v1:def",
                OptionsJson = ReadOnlyMemory<byte>.Empty,
                LicenseConfirmationJson = null,
                Status = TrainingRunStatus.Succeeded,
                ProgressJson = null,
                LogTail = null,
                LaunchReceiptJson = null,
                ErrorMessage = null,
                Version = 4,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                WorkStatus = TrainingWorkStatus.Succeeded,
                WorkErrorMessage = null
            };

        private static TrainingDatasetRecord Dataset(string contentFingerprint) =>
            new()
            {
                Id = DatasetId,
                DefinitionId = Guid.NewGuid(),
                DefinitionVersion = 1,
                DefinitionJson = null,
                Name = "dataset",
                Status = TrainingDatasetStatus.Ready,
                Revision = 1,
                ContentFingerprint = contentFingerprint,
                TotalSampleCount = 1,
                GoodSampleCount = 1,
                BadSampleCount = 0,
                RejectedSampleCount = 0,
                DuplicateSampleCount = 0,
                Version = 1,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                WorkStatus = DatasetGenerationWorkStatus.Succeeded,
                WorkErrorMessage = null
            };

        private static TrainingEvaluationRecord Evaluation() =>
            new()
            {
                Id = Guid.NewGuid(),
                TrainingRunId = RunId,
                ComparisonId = null,
                ModelName = "base:Q4_K_M",
                ModelContentFingerprint = null,
                DatasetId = DatasetId,
                DatasetContentFingerprint = FrozenFingerprint,
                MembershipJson = ReadOnlyMemory<byte>.Empty,
                Status = TrainingEvaluationStatus.Queued,
                ResultsJson = null,
                TotalCount = 1,
                ScoredCount = 0,
                PassedCount = 0,
                PerKindJson = null,
                ErrorMessage = null,
                Version = 1,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                WorkStatus = TrainingWorkStatus.Queued
            };
    }
}
