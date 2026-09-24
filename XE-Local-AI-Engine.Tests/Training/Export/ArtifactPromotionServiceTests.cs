namespace XE_Local_AI_Engine.Tests.Training.Export;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a promotion is allowed to commit, and what it must carry when it does. The lineage assertions are the
///     point: a registry entry with no derived-from is indistinguishable from an import, and the question "what was
///     this model trained on" becomes unanswerable the moment the run row is deleted.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ArtifactPromotionServiceTests : IDisposable
{
    private const string BaseModelName = "base:Q4_K_M";
    private static readonly string ArtifactSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("gguf")));
    private static readonly Guid ArtifactId = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid RunId = new("00000000-0000-0000-0000-0000000000b1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Arguments(TrainingArtifactSmokeState.Pending)]
    [Arguments(TrainingArtifactSmokeState.Failed)]
    [Arguments(TrainingArtifactSmokeState.Skipped)]
    public async Task Promote_WithoutAPassedSmokeTest_IsRefusedAndTouchesNothing(TrainingArtifactSmokeState state)
    {
        // The whole point of the gate: staged is inert. A model that could not prove it loads never reaches the
        // registry, whatever the operator clicks.
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, state);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "smoke test", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
        _ = await harness.Store.DidNotReceiveWithAnyArgs().SetArtifactCommittedNameAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task Promote_MergedModel_CommitsAsTrainedWithItsCheckpointLineage()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);

        var name = await harness.PromoteAsync();

        var destination = AssertEx.NotNull(harness.Destination, "The importer must be handed a destination.");
        AssertEx.Equal(LocalModelOrigin.Trained, destination.Origin);
        AssertEx.False(destination.IsAdapter, "A merged model is a standalone entry, not an adapter.");
        var lineage = AssertEx.NotNull(destination.Lineage, "A trained destination must carry lineage.");
        AssertEx.Equal("meta/base", lineage.DerivedFromRepoId);
        AssertEx.Equal("main", lineage.DerivedFromRevision);
        AssertEx.Equal("v1:dataset", lineage.DerivedFromContentFingerprint);
        AssertEx.Null(lineage.BaseModelName);
        AssertEx.Equal("tuned:Q4_K_M", name);
        _ = await harness.Store.Received(1).SetArtifactCommittedNameAsync(ArtifactId, Arg.Any<long>(), "tuned:Q4_K_M", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Promote_Adapter_NamesTheInstalledBaseModelItAppliesTo()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.AdapterGguf, TrainingArtifactSmokeState.Passed);

        _ = await harness.PromoteAsync();

        var destination = AssertEx.NotNull(harness.Destination, "The importer must be handed a destination.");
        AssertEx.True(destination.IsAdapter, "An adapter destination is identified by naming its base model.");
        AssertEx.Equal(BaseModelName, destination.Lineage!.BaseModelName);
        AssertEx.Equal(LocalModelOrigin.Trained, destination.Origin);
    }

    [Test]
    public async Task Promote_AdapterFromARunWithNoLinkedModel_IsRefused()
    {
        // An adapter carries no weights of its own. Without a base model to apply it to, the entry would be
        // permanently unlaunchable — so the refusal belongs here, not at the first failed load.
        var harness = Harness.Create(this, TrainingArtifactKind.AdapterGguf, TrainingArtifactSmokeState.Passed, linkedModel: null);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "no installed base counterpart", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    [Arguments(null, true, "v1:dataset")]
    [Arguments("other:Q4_K_M", true, "v1:dataset")]
    [Arguments(BaseModelName, false, "v1:dataset")]
    [Arguments(BaseModelName, true, "v1:different")]
    public async Task Promote_WhenExactInstalledBaseIsAbsent_DoesNotPrepareImport(string? installedName,
        bool isAvailable,
        string fingerprint)
    {
        var harness = Harness.Create(this,
            TrainingArtifactKind.MergedGguf,
            TrainingArtifactSmokeState.Passed,
            installedModelName: installedName,
            installedModelAvailable: isAvailable,
            installedModelFingerprint: fingerprint);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "exact installed base counterpart", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Promote_AnAlreadyRegisteredArtifact_IsRefused()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed, committedName: "tuned:Q4_K_M");

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "already registered", StringComparison.Ordinal);
    }

    [Test]
    public async Task Promote_WithoutQualityDecision_DoesNotPrepareAnImport()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed,
            qualityOutcome: null);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "quality decision", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Promote_WhileQualityRevalidationIsPending_DoesNotPrepareAnImport()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed,
            qualityOutcome: ArtifactQualityOutcome.Pending);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "quality", StringComparison.OrdinalIgnoreCase);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Promote_AcceptsDecisionPersistedByArtifactQualityService()
    {
        var harness = Harness.Create(this,
            TrainingArtifactKind.MergedGguf,
            TrainingArtifactSmokeState.Passed,
            qualityOutcome: null);

        var decided = await harness.DecideQualityAsync();
        var promotedName = await harness.PromoteAsync();

        AssertEx.Equal(ArtifactQualityOutcome.Passed, ArtifactQualityService.ReadDecision(decided)!.Outcome);
        AssertEx.Equal("tuned:Q4_K_M", promotedName);
        _ = await harness.Importer.Received(1).CommitAsync(Arg.Any<PreparedGgufImport>(), CancellationToken.None);
    }

    [Test]
    public async Task Promote_WhenBytesChangedAfterDecision_DoesNotPrepareAnImport()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        await File.AppendAllTextAsync(harness.StagedPath, "changed");

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "changed", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    [Arguments("different-digest", 4L)]
    [Arguments(null, 5L)]
    public async Task Promote_WhenPreparedIdentityDoesNotMatchDecision_DiscardsWithoutCommit(string? sha256, long sizeBytes)
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        harness.ReturnPreparedIdentity(sha256 ?? ArtifactSha256, sizeBytes);

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "changed", StringComparison.Ordinal);
        await harness.Importer.Received(1).DiscardPreparedAsync(Arg.Any<PreparedGgufImport>(), CancellationToken.None);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().CommitAsync(default!, default);
    }

    [Test]
    public async Task Promote_WhenCommitLeavesFinalArtifacts_RollsBackTheCommitReceipt()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        var receipt = harness.CommitReceipt() with
        {
            OwnsFinalGguf = true,
            OwnsFinalSidecar = false
        };
        harness.ThrowPartialCommit(receipt);

        _ = await AssertEx.ThrowsAsync<GgufImportCommitException>(() => harness.PromoteAsync());

        await harness.Importer.Received(1).RollbackCommittedAsync(receipt, CancellationToken.None);
        await harness.Importer.DidNotReceiveWithAnyArgs().DiscardPreparedAsync(default!, default);
    }

    [Test]
    public async Task Promote_WhenPartialCommitRollbackAlsoFails_PreservesBothFailures()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        var receipt = harness.CommitReceipt();
        harness.ThrowPartialCommit(receipt);
        _ = harness.Importer.RollbackCommittedAsync(receipt, CancellationToken.None)
                   .Returns<Task>(_ => throw new IOException("rollback failed"));

        var failure = await AssertEx.ThrowsAsync<AggregateException>(() => harness.PromoteAsync());

        AssertEx.True(failure.InnerExceptions.Any(static exception => exception is GgufImportCommitException));
        AssertEx.True(failure.InnerExceptions.Any(static exception => exception is IOException));
    }

    [Test]
    public async Task Promote_WhenRecordingTheCommitFails_RollsBackTheRegistryEntry()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        _ = harness.Store.SetArtifactCommittedNameAsync(ArtifactId, Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("recording failed"));

        _ = await AssertEx.ThrowsAsync<TrainingConflictException>(() => harness.PromoteAsync());

        await harness.Importer.Received(1).RollbackCommittedAsync(Arg.Any<GgufImportCommitReceipt>(), CancellationToken.None);
    }

    [Test]
    public async Task Promote_WhenRecordingAndRollbackFail_PreservesBothFailuresAndRecoveryReceipt()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed);
        var receipt = harness.CommitReceipt();
        harness.ReturnCommitReceipt(receipt);
        _ = harness.Store.SetArtifactCommittedNameAsync(ArtifactId, Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("recording failed"));
        _ = harness.Importer.RollbackCommittedAsync(receipt, CancellationToken.None)
                   .Returns<Task>(_ => throw new IOException("rollback failed"));

        var failure = await AssertEx.ThrowsAsync<ArtifactPromotionCompensationException>(() => harness.PromoteAsync());

        AssertEx.Equal(receipt, failure.CommitReceipt);
        AssertEx.True(failure.InnerExceptions.Any(static exception => exception is TrainingConflictException));
        AssertEx.True(failure.InnerExceptions.Any(static exception => exception is IOException));
    }

    private sealed class Harness
    {
        private ArtifactPromotionService _service = null!;
        private TrainingArtifactRecord _artifact = null!;
        private TrainingRunRecord _run = null!;

        private Harness(ITrainingRunStore store, IGgufModelImporter importer, string stagedPath)
        {
            Store = store;
            Importer = importer;
            StagedPath = stagedPath;
        }

        public ITrainingRunStore Store { get; }
        public IGgufModelImporter Importer { get; }
        public string StagedPath { get; }
        public GgufImportDestination? Destination { get; private set; }

        public static Harness Create(ArtifactPromotionServiceTests owner,
            TrainingArtifactKind kind,
            TrainingArtifactSmokeState smokeState,
            string? linkedModel = BaseModelName,
            string? committedName = null,
            ArtifactQualityOutcome? qualityOutcome = ArtifactQualityOutcome.Passed,
            string? installedModelName = BaseModelName,
            bool installedModelAvailable = true,
            string installedModelFingerprint = "v1:dataset")
        {
            _ = Directory.CreateDirectory(owner._root);
            var stagedPath = Path.Combine(owner._root, kind == TrainingArtifactKind.AdapterGguf ? "adapter-F16.gguf" : "merged-Q4_K_M.gguf");
            File.WriteAllText(stagedPath, "gguf");
            var sha256 = ArtifactSha256;
            var comparisonId = Guid.NewGuid();
            ReadOnlyMemory<byte>? qualityJson = qualityOutcome is { } outcome
                ? JsonSerializer.SerializeToUtf8Bytes(new ArtifactQualityDecisionV1
                {
                    ArtifactId = ArtifactId,
                    ArtifactSha256 = sha256,
                    ComparisonId = comparisonId,
                    Outcome = outcome
                }, TrainingJson.Options)
                : null;

            var store = Substitute.For<ITrainingRunStore>();
            var artifact = new TrainingArtifactRecord
            {
                Id = ArtifactId,
                RunId = RunId,
                Kind = kind,
                Path = stagedPath,
                Sha256 = sha256,
                SizeBytes = 4,
                SmokeState = smokeState,
                SmokeReason = null,
                CommittedModelName = committedName,
                Version = 2,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                QualityComparisonId = comparisonId,
                QualityDecisionJson = qualityJson
            };
            var run = Run(linkedModel);
            _ = store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(_ => artifact);
            _ = store.GetAsync(RunId, Arg.Any<CancellationToken>()).Returns(run);
            _ = store.SetArtifactCommittedNameAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo =>
                     {
                         artifact = artifact with
                         {
                             CommittedModelName = callInfo.ArgAt<string?>(2),
                             Version = artifact.Version + 1
                         };
                         return artifact;
                     });

            var baseArtifacts = Substitute.For<ITrainingBaseArtifactStore>();
            _ = baseArtifacts.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                             .Returns(new TrainingBaseArtifactRecord
                             {
                                 Id = Guid.NewGuid(),
                                 RepoId = "meta/base",
                                 Revision = "main",
                                 Status = TrainingBaseArtifactStatus.Ready,
                                 FilesJson = ReadOnlyMemory<byte>.Empty,
                                 TotalBytes = 0,
                                 LicenseJson = null,
                                 ErrorMessage = null,
                                 Version = 1,
                                 CreatedAtUtc = 0,
                                 UpdatedAtUtc = 0
                             });
            var models = Substitute.For<IGgufModelStore>();
            _ = models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<LocalModelDescriptor>>(installedModelName is null
                ? []
                :
                [
                    new LocalModelDescriptor
                    {
                        ModelName = installedModelName,
                        ProviderName = "llamacpp",
                        IsAvailable = installedModelAvailable,
                        SizeBytes = 4,
                        ModifiedAt = null,
                        MaxContextTokens = null,
                        ModelContentFingerprint = installedModelFingerprint
                    }
                ]);

            var identity = new ResolvedGgufAcquisitionIdentity
            {
                CanonicalModelName = "tuned:Q4_K_M",
                ModelReservationKey = "tuned:q4_k_m",
                CanonicalQuantization = kind == TrainingArtifactKind.AdapterGguf ? "F16" : "Q4_K_M",
                FinalFileName = "tuned-Q4_K_M-abc.gguf",
                RelativeGgufPath = "tuned-Q4_K_M-abc.gguf",
                RelativeSidecarPath = "tuned-Q4_K_M-abc.gguf.xe-model.json",
                ProjectorFileName = null,
                ProjectorRelativePath = null
            };
            var preflight = Substitute.For<IGgufAcquisitionPreflight>();
            _ = preflight.ResolveAndReserveAsync(Arg.Any<GgufAcquisitionIntent>(), Arg.Any<CancellationToken>())
                         .Returns(Reservation(identity));

            var importer = Substitute.For<IGgufModelImporter>();
            var harness = new Harness(store, importer, stagedPath)
            {
                _artifact = artifact,
                _run = run
            };
            _ = store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(_ => harness._artifact);
            _ = store.SetArtifactQualityDecisionAsync(ArtifactId, Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(),
                         Arg.Any<CancellationToken>())
                     .Returns(callInfo =>
                     {
                         harness._artifact = harness._artifact with
                         {
                             Version = harness._artifact.Version + 1,
                             QualityComparisonId = callInfo.ArgAt<Guid>(2),
                             QualityDecisionJson = callInfo.ArgAt<ReadOnlyMemory<byte>>(3)
                         };
                         return harness._artifact;
                     });
            _ = importer.PrepareAsync(Arg.Any<GgufImportSource>(), Arg.Any<GgufImportDestination>(), Arg.Any<IProgress<GgufImportProgress>?>(),
                            Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            harness.Destination = callInfo.Arg<GgufImportDestination>();
                            return Task.FromResult(Prepared(callInfo.Arg<GgufImportDestination>()));
                        });
            _ = importer.CommitAsync(Arg.Any<PreparedGgufImport>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new GgufImportCommitReceipt
                        {
                            RegistryEntry = callInfo.Arg<PreparedGgufImport>().RegistryEntry,
                            FinalGgufPath = "/models/tuned.gguf",
                            FinalSidecarPath = "/models/tuned.gguf.xe-model.json",
                            WeightMemberFingerprint = "member",
                            ModelContentFingerprint = "v1:content"
                        }));

            harness._service = new ArtifactPromotionService(store, baseArtifacts, models, preflight, importer,
                NullLogger<ArtifactPromotionService>.Instance);
            return harness;
        }

        public Task<string> PromoteAsync() =>
            _service.PromoteAsync(ArtifactId, "tuned");

        public Task<TrainingArtifactRecord> DecideQualityAsync()
        {
            var membership = JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationMembershipV1
            {
                TrainingRunId = RunId,
                FreezeId = Guid.NewGuid(),
                DatasetId = _run.DatasetId,
                DatasetContentFingerprint = _run.DatasetContentFingerprint,
                HoldoutSampleIds = [Guid.NewGuid()]
            }, TrainingJson.Options);
            var baseProvenance = JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationExecutionProvenanceV1
            {
                Variant = "Cuda",
                ExecutableVersion = "v1",
                ExecutableSha256 = new string('c', 64),
                ManifestSha256 = new string('c', 64),
                LaunchProjectionIdentity = "projection",
                ContextTokens = 4096,
                LaunchPolicyVersion = 1,
                ModelSha256 = new string('d', 64),
                ModelSizeBytes = 4
            }, TrainingJson.Options);
            var tunedProvenance = JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationExecutionProvenanceV1
            {
                Variant = "Cuda",
                ExecutableVersion = "v1",
                ExecutableSha256 = new string('c', 64),
                ManifestSha256 = new string('c', 64),
                LaunchProjectionIdentity = "projection",
                ContextTokens = 4096,
                LaunchPolicyVersion = 1,
                ModelSha256 = ArtifactSha256,
                ModelSizeBytes = 4
            }, TrainingJson.Options);
            var baseEvaluation = Evaluation("base:Q4_K_M", "v1:dataset", membership, baseProvenance,
                EvaluationModelTargetKind.InstalledModel, sourceArtifactId: null);
            var tunedEvaluation = Evaluation("tuned.gguf", ArtifactSha256, membership, tunedProvenance,
                EvaluationModelTargetKind.StagedTrainingArtifact, ArtifactId);
            var comparison = new TrainingComparisonRecord
            {
                Id = Guid.NewGuid(),
                Name = "quality",
                BaseEvaluationRunId = baseEvaluation.Id,
                TunedEvaluationRunId = tunedEvaluation.Id,
                BaseBenchmarkRunId = null,
                TunedBenchmarkRunId = null,
                TrainingRunId = RunId,
                DeltasJson = JsonSerializer.SerializeToUtf8Bytes(ComparisonReportService.ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark: null, tunedBenchmark: null),
                    TrainingJson.Options),
                Version = 1,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0
            };
            var evaluations = Substitute.For<ITrainingEvaluationStore>();
            _ = evaluations.GetComparisonAsync(comparison.Id, Arg.Any<CancellationToken>()).Returns(comparison);
            _ = evaluations.GetAsync(baseEvaluation.Id, Arg.Any<CancellationToken>()).Returns(baseEvaluation);
            _ = evaluations.GetAsync(tunedEvaluation.Id, Arg.Any<CancellationToken>()).Returns(tunedEvaluation);
            return new ArtifactQualityService(Store, evaluations, TimeProvider.System)
                .DecideAsync(ArtifactId, comparison.Id, _artifact.Version);
        }

        private static TrainingEvaluationRecord Evaluation(string modelName,
            string fingerprint,
            ReadOnlyMemory<byte> membership,
            ReadOnlyMemory<byte> provenance,
            EvaluationModelTargetKind targetKind,
            Guid? sourceArtifactId) =>
            new()
            {
                Id = Guid.NewGuid(),
                TrainingRunId = RunId,
                ComparisonId = Guid.NewGuid(),
                ModelName = modelName,
                ModelContentFingerprint = fingerprint,
                DatasetId = Guid.NewGuid(),
                DatasetContentFingerprint = "v1:dataset",
                MembershipJson = membership,
                Status = TrainingEvaluationStatus.Succeeded,
                ResultsJson = TrainingEvaluationResults.Write([
                    new TrainingEvaluationResultEntry
                    {
                        SampleId = Guid.NewGuid(),
                        Kind = "tool",
                        Passed = true,
                        ScoredBy = "deterministic"
                    }
                ]),
                TotalCount = 1,
                ScoredCount = 1,
                PassedCount = 1,
                PerKindJson = null,
                ErrorMessage = null,
                Version = 2,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                WorkStatus = TrainingWorkStatus.Succeeded,
                TargetKind = targetKind,
                SourceArtifactId = sourceArtifactId,
                ExecutionProvenanceJson = provenance
            };

        public void ReturnPreparedIdentity(string sha256, long sizeBytes) =>
            _ = Importer.PrepareAsync(Arg.Any<GgufImportSource>(), Arg.Any<GgufImportDestination>(), Arg.Any<IProgress<GgufImportProgress>?>(),
                            Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            Destination = callInfo.Arg<GgufImportDestination>();
                            return Task.FromResult(Prepared(Destination, sha256, sizeBytes));
                        });

        public GgufImportCommitReceipt CommitReceipt() =>
            new()
            {
                RegistryEntry = Prepared(Destination ?? new GgufImportDestination
                {
                    CanonicalModelName = "tuned:Q4_K_M",
                    CanonicalQuant = "Q4_K_M",
                    RelativeGgufPath = "tuned.gguf",
                    RelativeSidecarPath = "tuned.json",
                    Origin = LocalModelOrigin.Trained
                }).RegistryEntry,
                FinalGgufPath = "/models/tuned.gguf",
                FinalSidecarPath = "/models/tuned.gguf.xe-model.json",
                WeightMemberFingerprint = "member",
                ModelContentFingerprint = "v1:content"
            };

        public void ThrowPartialCommit(GgufImportCommitReceipt receipt) =>
            _ = Importer.CommitAsync(Arg.Any<PreparedGgufImport>(), CancellationToken.None)
                        .Returns<Task<GgufImportCommitReceipt>>(_ => throw new GgufImportCommitException(receipt,
                            "commit failed",
                            new IOException("provider failure")));

        public void ReturnCommitReceipt(GgufImportCommitReceipt receipt) =>
            _ = Importer.CommitAsync(Arg.Any<PreparedGgufImport>(), CancellationToken.None).Returns(receipt);

        private static PreparedGgufAcquisition Reservation(ResolvedGgufAcquisitionIdentity identity) =>
            new(identity, GgufAcquisitionDisposition.Available, ProviderMapDisposition.Absent, lease: null, activeOperationId: null);

        private static PreparedGgufImport Prepared(GgufImportDestination destination,
            string? sha256 = null,
            long sizeBytes = 4) =>
            new()
            {
                OperationId = "op",
                Destination = destination,
                TemporaryGgufPath = "/tmp/tuned.gguf.part",
                TemporarySidecarPath = "/tmp/tuned.gguf.xe-model.json.part",
                RegistryEntry = new GgufModelRegistryEntry
                {
                    ModelName = destination.CanonicalModelName,
                    RepoId = destination.CanonicalModelName,
                    FileName = "tuned-Q4_K_M-abc.gguf",
                    Quant = destination.CanonicalQuant,
                    LocalPath = "/models/tuned.gguf",
                    SizeBytes = sizeBytes,
                    Sha256 = sha256 ?? ArtifactSha256,
                    SourceRevision = $"sha256:{sha256 ?? ArtifactSha256}",
                    DownloadedAtUtc = DateTimeOffset.UnixEpoch,
                    Origin = destination.Origin
                },
                Sidecar = Sidecar(destination),
                WeightMemberFingerprint = "member",
                ModelContentFingerprint = "v1:content"
            };

        private static GgufAcquisitionMetadata Sidecar(GgufImportDestination destination) =>
            new()
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = "v1:0",
                ModelName = destination.CanonicalModelName,
                Origin = destination.Origin,
                LocalFileName = "tuned-Q4_K_M-abc.gguf",
                Quantization = destination.CanonicalQuant,
                WeightContentSha256 = "abc",
                WeightSizeBytes = 4,
                WeightMemberFingerprint = "member",
                SourceDisplayName = "merged-Q4_K_M.gguf",
                AcquiredAtUtc = DateTimeOffset.UnixEpoch,
                RegistryRepoId = destination.CanonicalModelName,
                RegistrySourceRevision = "sha256:abc",
                Role = GgufRole.Chat,
                ModelContentFingerprint = "v1:content"
            };

        private static TrainingRunRecord Run(string? linkedModel) =>
            new()
            {
                Id = RunId,
                DatasetId = Guid.NewGuid(),
                DatasetContentFingerprint = "v1:abc",
                DatasetRevision = 1,
                FreezeJson = ReadOnlyMemory<byte>.Empty,
                BaseArtifactId = Guid.NewGuid(),
                LinkedInstalledModelName = linkedModel,
                LinkedModelContentFingerprint = "v1:dataset",
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
    }
}
