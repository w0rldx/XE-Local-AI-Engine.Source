namespace XE_Local_AI_Engine.Tests.Training.Export;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a promotion is allowed to commit, and what it must carry when it does. The lineage assertions are the
///     point: a registry entry with no derived-from is indistinguishable from an import, and the question "what was
///     this model trained on" becomes unanswerable the moment the run row is deleted.
/// </summary>
public sealed class ArtifactPromotionServiceTests : IDisposable
{
    private const string BaseModelName = "base:Q4_K_M";
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

        AssertEx.Contains(failure.Message, "not linked to an installed model", StringComparison.Ordinal);
        _ = await harness.Importer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Promote_AnAlreadyRegisteredArtifact_IsRefused()
    {
        var harness = Harness.Create(this, TrainingArtifactKind.MergedGguf, TrainingArtifactSmokeState.Passed, committedName: "tuned:Q4_K_M");

        var failure = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() => harness.PromoteAsync());

        AssertEx.Contains(failure.Message, "already registered", StringComparison.Ordinal);
    }

    private sealed class Harness
    {
        private ArtifactPromotionService _service = null!;

        private Harness(ITrainingRunStore store, IGgufModelImporter importer)
        {
            Store = store;
            Importer = importer;
        }

        public ITrainingRunStore Store { get; }
        public IGgufModelImporter Importer { get; }
        public GgufImportDestination? Destination { get; private set; }

        public static Harness Create(ArtifactPromotionServiceTests owner,
            TrainingArtifactKind kind,
            TrainingArtifactSmokeState smokeState,
            string? linkedModel = BaseModelName,
            string? committedName = null)
        {
            _ = Directory.CreateDirectory(owner._root);
            var stagedPath = Path.Combine(owner._root, kind == TrainingArtifactKind.AdapterGguf ? "adapter-F16.gguf" : "merged-Q4_K_M.gguf");
            File.WriteAllText(stagedPath, "gguf");

            var store = Substitute.For<ITrainingRunStore>();
            _ = store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>())
                     .Returns(new TrainingArtifactRecord(ArtifactId, RunId, kind, stagedPath, "abc", SizeBytes: 4, smokeState,
                         SmokeReason: null, committedName, Version: 2, CreatedAtUtc: 0, UpdatedAtUtc: 0));
            _ = store.GetAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run(linkedModel));
            _ = store.SetArtifactCommittedNameAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult(new TrainingArtifactRecord(ArtifactId, RunId, kind, stagedPath, "abc", 4, smokeState,
                         null, callInfo.ArgAt<string?>(2), 3, 0, 0)));

            var baseArtifacts = Substitute.For<ITrainingBaseArtifactStore>();
            _ = baseArtifacts.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                             .Returns(new TrainingBaseArtifactRecord(Guid.NewGuid(), "meta/base", "main", TrainingBaseArtifactStatus.Ready,
                                 ReadOnlyMemory<byte>.Empty, TotalBytes: 0, LicenseJson: null, ErrorMessage: null, Version: 1,
                                 CreatedAtUtc: 0, UpdatedAtUtc: 0));

            var identity = new ResolvedGgufAcquisitionIdentity("tuned:Q4_K_M",
                "tuned:q4_k_m",
                kind == TrainingArtifactKind.AdapterGguf ? "F16" : "Q4_K_M",
                "tuned-Q4_K_M-abc.gguf",
                "tuned-Q4_K_M-abc.gguf",
                "tuned-Q4_K_M-abc.gguf.xe-model.json",
                ProjectorFileName: null,
                ProjectorRelativePath: null);
            var preflight = Substitute.For<IGgufAcquisitionPreflight>();
            _ = preflight.ResolveAndReserveAsync(Arg.Any<GgufAcquisitionIntent>(), Arg.Any<CancellationToken>())
                         .Returns(Reservation(identity));

            var importer = Substitute.For<IGgufModelImporter>();
            var harness = new Harness(store, importer);
            _ = importer.PrepareAsync(Arg.Any<GgufImportSource>(), Arg.Any<GgufImportDestination>(), Arg.Any<IProgress<GgufImportProgress>?>(),
                            Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            harness.Destination = callInfo.Arg<GgufImportDestination>();
                            return Task.FromResult(Prepared(callInfo.Arg<GgufImportDestination>()));
                        });
            _ = importer.CommitAsync(Arg.Any<PreparedGgufImport>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(new GgufImportCommitReceipt(callInfo.Arg<PreparedGgufImport>().RegistryEntry,
                            "/models/tuned.gguf",
                            "/models/tuned.gguf.xe-model.json",
                            "member",
                            "v1:content")));

            harness._service = new ArtifactPromotionService(store, baseArtifacts, preflight, importer,
                NullLogger<ArtifactPromotionService>.Instance);
            return harness;
        }

        public Task<string> PromoteAsync() =>
            _service.PromoteAsync(ArtifactId, "tuned");

        private static PreparedGgufAcquisition Reservation(ResolvedGgufAcquisitionIdentity identity) =>
            new(identity, GgufAcquisitionDisposition.Available, ProviderMapDisposition.Absent, lease: null, activeOperationId: null);

        private static PreparedGgufImport Prepared(GgufImportDestination destination) =>
            new("op",
                destination,
                "/tmp/tuned.gguf.part",
                "/tmp/tuned.gguf.xe-model.json.part",
                new GgufModelRegistryEntry
                {
                    ModelName = destination.CanonicalModelName,
                    RepoId = destination.CanonicalModelName,
                    FileName = "tuned-Q4_K_M-abc.gguf",
                    Quant = destination.CanonicalQuant,
                    LocalPath = "/models/tuned.gguf",
                    SizeBytes = 4,
                    Sha256 = "abc",
                    SourceRevision = "sha256:abc",
                    DownloadedAtUtc = DateTimeOffset.UnixEpoch,
                    Origin = destination.Origin
                },
                Sidecar(destination),
                "member",
                "v1:content");

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
            new(RunId,
                Guid.NewGuid(),
                "v1:abc",
                DatasetRevision: 1,
                FreezeJson: ReadOnlyMemory<byte>.Empty,
                BaseArtifactId: Guid.NewGuid(),
                linkedModel,
                LinkedModelContentFingerprint: "v1:dataset",
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
    }
}
