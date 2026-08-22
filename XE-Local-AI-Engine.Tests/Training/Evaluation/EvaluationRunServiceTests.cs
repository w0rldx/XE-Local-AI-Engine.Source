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

        _ = await harness.Service.CreateAsync(new CreateEvaluationCommand(RunId, EvaluationTarget.Base));

        var enqueued = AssertEx.NotNull(harness.Enqueued);
        AssertEx.Equal(FrozenFingerprint, enqueued.DatasetContentFingerprint);
        var membership = JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(enqueued.MembershipJson.Span, TrainingJson.Options)!;
        AssertEx.Equal(HoldoutSampleId, membership.HoldoutSampleIds.Single());
    }

    [Test]
    public async Task Create_WhenTheDatasetStillMatchesTheFreeze_EnqueuesTheFrozenMembership()
    {
        var harness = Harness.Create(datasetFingerprint: FrozenFingerprint);

        _ = await harness.Service.CreateAsync(new CreateEvaluationCommand(RunId, EvaluationTarget.Base));

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
            harness.Service.CreateAsync(new CreateEvaluationCommand(RunId, EvaluationTarget.Undefined)));

        AssertEx.Contains(exception.Message, "target is required", StringComparison.OrdinalIgnoreCase);
        _ = await harness.Evaluations.DidNotReceiveWithAnyArgs()
                         .CreateAndEnqueueAsync(default!, CancellationToken.None);
    }

    /// <summary>One service over substituted stores; only the irrelevant live dataset fingerprint varies.</summary>
    private sealed class Harness
    {
        private Harness(EvaluationRunService service, ITrainingEvaluationStore evaluations)
        {
            Service = service;
            Evaluations = evaluations;
        }

        public EvaluationRunService Service { get; }

        public ITrainingEvaluationStore Evaluations { get; }

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
            var harness = new Harness(new EvaluationRunService(evaluations, runs, datasets, models,
                new TrainingRunCancellationRegistry(), Substitute.For<ITrainingRunQueueSignal>()), evaluations);
            _ = evaluations.CreateAndEnqueueAsync(Arg.Any<TrainingEvaluationEnqueueCommand>(), Arg.Any<CancellationToken>())
                           .Returns(callInfo =>
                           {
                               harness.Enqueued = callInfo.Arg<TrainingEvaluationEnqueueCommand>();
                               return Task.FromResult(Evaluation());
                           });
            return harness;
        }

        private static TrainingRunRecord Run(TrainingRunFreezeV1 freeze) =>
            new(RunId,
                DatasetId,
                FrozenFingerprint,
                DatasetRevision: 1,
                JsonSerializer.SerializeToUtf8Bytes(freeze, TrainingJson.Options),
                BaseArtifactId: Guid.NewGuid(),
                LinkedInstalledModelName: "base:Q4_K_M",
                LinkedModelContentFingerprint: "v1:def",
                OptionsJson: ReadOnlyMemory<byte>.Empty,
                LicenseConfirmationJson: null,
                TrainingRunStatus.Succeeded,
                ProgressJson: null,
                LogTail: null,
                LaunchReceiptJson: null,
                ErrorMessage: null,
                Version: 4,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                TrainingWorkStatus.Succeeded,
                WorkErrorMessage: null);

        private static TrainingDatasetRecord Dataset(string contentFingerprint) =>
            new(DatasetId, Guid.NewGuid(), 1, DefinitionJson: null, "dataset", TrainingDatasetStatus.Ready, 1, contentFingerprint,
                1, 1, 0, 0, 0, 1, 0, 0, DatasetGenerationWorkStatus.Succeeded, null);

        private static TrainingEvaluationRecord Evaluation() =>
            new(Guid.NewGuid(),
                RunId,
                ComparisonId: null,
                "base:Q4_K_M",
                ModelContentFingerprint: null,
                DatasetId,
                FrozenFingerprint,
                MembershipJson: ReadOnlyMemory<byte>.Empty,
                TrainingEvaluationStatus.Queued,
                ResultsJson: null,
                TotalCount: 1,
                ScoredCount: 0,
                PassedCount: 0,
                PerKindJson: null,
                ErrorMessage: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                TrainingWorkStatus.Queued);
    }
}
