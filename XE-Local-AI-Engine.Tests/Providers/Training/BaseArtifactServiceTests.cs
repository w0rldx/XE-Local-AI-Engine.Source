namespace XE_Local_AI_Engine.Tests.Providers.Training;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the orchestration guards around base-checkpoint acquisition: the disk preflight, the delete guard while a
///     transfer is live, and the cancel path.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BaseArtifactServiceTests : IDisposable
{
    private const string RepoId = "unsloth/Llama-3.2-1B-Instruct";

    private readonly BaseArtifactDownloadCoordinator _coordinator;
    private readonly IBaseCheckpointStore _checkpointStore = Substitute.For<IBaseCheckpointStore>();
    private readonly IFreeSpaceProbe _freeSpaceProbe = Substitute.For<IFreeSpaceProbe>();
    private readonly IJobCreatedPublisher _jobs = Substitute.For<IJobCreatedPublisher>();
    private readonly IModelLabActiveJob _labJob = Substitute.For<IModelLabActiveJob>();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-base-svc-" + Guid.NewGuid().ToString("N"));
    private readonly ITrainingBaseArtifactStore _store = Substitute.For<ITrainingBaseArtifactStore>();

    public BaseArtifactServiceTests()
    {
        var dataDirectory = Substitute.For<INodeDataDirectory>();
        _ = dataDirectory.Root.Returns(_root);
        DataDirectory = dataDirectory;
        _coordinator = new BaseArtifactDownloadCoordinator(Substitute.For<IServiceScopeFactory>(),
            _checkpointStore,
            dataDirectory,
            _jobs,
            NullLogger<BaseArtifactDownloadCoordinator>.Instance);
    }

    private INodeDataDirectory DataDirectory { get; }

    public void Dispose()
    {
        _coordinator.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task StartDownload_WhenTheVolumeCannotHoldTheCheckpoint_RefusesBeforeRecordingAnything()
    {
        const long manifestBytes = 30L * 1024 * 1024 * 1024;
        _ = _checkpointStore.ResolveAsync(RepoId, null, Arg.Any<CancellationToken>()).Returns(Manifest(manifestBytes));

        // Enough for the weights themselves, but not for the headroom the frozen dataset, work directory and export
        // all need on the same volume immediately afterwards.
        _ = _freeSpaceProbe.GetAvailableFreeBytes(_root).Returns(manifestBytes + 1);

        var exception = await AssertEx.ThrowsAsync<BaseArtifactRejectedException>(() => Service().StartDownloadAsync(RepoId, revision: null, CancellationToken.None));

        AssertEx.Contains(exception.Message, "free space");
        await _store.DidNotReceive().StartDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartDownload_WhenTheRepoIsNotTrainable_SurfacesTheRejectionUntouched()
    {
        _ = _checkpointStore.ResolveAsync(RepoId, null, Arg.Any<CancellationToken>())
                            .Returns<Task<BaseCheckpointManifest>>(_ =>
                                throw new BaseCheckpointNotTrainableException("The selected repository has no safetensors weights."));

        var exception = await AssertEx.ThrowsAsync<BaseArtifactRejectedException>(() => Service().StartDownloadAsync(RepoId, revision: null, CancellationToken.None));

        AssertEx.Contains(exception.Message, "safetensors");
    }

    /// <summary>The training path records what the store resolved, so "main" never becomes an artifact's identity.</summary>
    [Test]
    public async Task StartDownload_RecordsTheResolvedCommit_NotTheRequestedBranch()
    {
        const string Commit = "2fc06364715b967f1860aea9cf38778875588b17";
        _ = _checkpointStore.ResolveAsync(RepoId, "main", Arg.Any<CancellationToken>())
                            .Returns(Manifest(1024) with
                            {
                                Revision = Commit
                            });
        _ = _freeSpaceProbe.GetAvailableFreeBytes(_root).Returns(long.MaxValue);
        _ = _store.StartDownloadAsync(RepoId, Commit, Arg.Any<CancellationToken>())
                  .Returns(Record(TrainingBaseArtifactStatus.Ready, Commit));

        var view = await Service().StartDownloadAsync(RepoId, "main", CancellationToken.None);

        AssertEx.Equal(Commit, view.Revision);
        await _store.DidNotReceive().StartDownloadAsync(RepoId, "main", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartDownload_WhenTheArtifactIsAlreadyReady_ReturnsItWithoutRedownloading()
    {
        _ = _checkpointStore.ResolveAsync(RepoId, null, Arg.Any<CancellationToken>()).Returns(Manifest(1024));
        _ = _freeSpaceProbe.GetAvailableFreeBytes(_root).Returns(long.MaxValue);
        _ = _store.StartDownloadAsync(RepoId, "main", Arg.Any<CancellationToken>())
                  .Returns(Record(TrainingBaseArtifactStatus.Ready));

        var view = await Service().StartDownloadAsync(RepoId, revision: null, CancellationToken.None);

        AssertEx.Equal(nameof(TrainingBaseArtifactStatus.Ready), view.Status);
        await _checkpointStore.DidNotReceive()
                              .DownloadAsync(Arg.Any<BaseCheckpointManifest>(),
                                  Arg.Any<string>(),
                                  Arg.Any<IProgress<PullProgress>>(),
                                  Arg.Any<CancellationToken>());
    }

    /// <summary>The download joins no hub group, so the operations feed learns of both ends only from these broadcasts.</summary>
    [Test]
    public async Task StartDownload_AnnouncesTheStartAndThenTheEnd_EvenWhenTheTransferFails()
    {
        var record = Record(TrainingBaseArtifactStatus.Downloading);
        _ = _checkpointStore.ResolveAsync(RepoId, null, Arg.Any<CancellationToken>()).Returns(Manifest(1024));
        _ = _freeSpaceProbe.GetAvailableFreeBytes(_root).Returns(long.MaxValue);
        _ = _store.StartDownloadAsync(RepoId, "main", Arg.Any<CancellationToken>()).Returns(record);
        _ = _checkpointStore.DownloadAsync(Arg.Any<BaseCheckpointManifest>(), Arg.Any<string>(), Arg.Any<IProgress<PullProgress>>(), Arg.Any<CancellationToken>())
                            .Returns<Task<BaseCheckpointManifest>>(_ => throw new HttpRequestException("network down"));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _jobs.PublishEndedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     ended.TrySetResult();
                     return Task.CompletedTask;
                 });

        _ = await Service().StartDownloadAsync(RepoId, revision: null, CancellationToken.None);
        await ended.Task.WaitAsync(TestBudgets.Contended);

        Received.InOrder(() =>
        {
            _ = _jobs.PublishCreatedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>());
            _ = _jobs.PublishEndedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>A finished transfer whose Ready write fails still announces its end, and announces it once.</summary>
    [Test]
    public async Task StartDownload_AnnouncesTheEndOnce_WhenTheTransferSucceedsButItsTerminalWriteFails()
    {
        var record = Record(TrainingBaseArtifactStatus.Downloading);
        var manifest = Manifest(1024);
        _ = _checkpointStore.ResolveAsync(RepoId, null, Arg.Any<CancellationToken>()).Returns(manifest);
        _ = _freeSpaceProbe.GetAvailableFreeBytes(_root).Returns(long.MaxValue);
        _ = _store.StartDownloadAsync(RepoId, "main", Arg.Any<CancellationToken>()).Returns(record);
        // The coordinator's scope factory is a bare substitute, so the store write that follows this transfer throws.
        _ = _checkpointStore.DownloadAsync(Arg.Any<BaseCheckpointManifest>(), Arg.Any<string>(), Arg.Any<IProgress<PullProgress>>(), Arg.Any<CancellationToken>())
                            .Returns(manifest);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _jobs.PublishEndedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     ended.TrySetResult();
                     return Task.CompletedTask;
                 });

        _ = await Service().StartDownloadAsync(RepoId, revision: null, CancellationToken.None);
        await ended.Task.WaitAsync(TestBudgets.Contended);
        await _coordinator.DrainAsync(record.Id, CancellationToken.None);

        await _jobs.Received(1).PublishCreatedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>());
        await _jobs.Received(1).PublishEndedAsync(CreatedJobKind.BaseArtifactDownload, record.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WhileTheDownloadIsStillRunning_IsRefused()
    {
        var record = Record(TrainingBaseArtifactStatus.Downloading);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);

        var outcome = await Service().DeleteAsync(record.Id, CancellationToken.None);

        AssertEx.Equal(BaseArtifactDeleteOutcome.Downloading, outcome);
        await _store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _ = _labJob.DidNotReceive().ClaimForDelete(Arg.Any<Guid>());
    }

    [Test]
    public async Task Delete_WhenTheArtifactDoesNotExist_ThrowsTrainingNotFound()
    {
        _ = _store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TrainingBaseArtifactRecord?)null);

        _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(() => Service().DeleteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task GetAndLicense_WhenTheArtifactDoesNotExist_ThrowTrainingNotFound()
    {
        _ = _store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TrainingBaseArtifactRecord?)null);

        _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(() => Service().GetAsync(Guid.NewGuid(), CancellationToken.None));
        _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(() => Service().GetLicenseAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task Delete_WhenReady_RemovesTheRowAndItsDirectory()
    {
        var record = Record(TrainingBaseArtifactStatus.Ready);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);
        _ = _store.DeleteAsync(record.Id, record.Version, Arg.Any<CancellationToken>()).Returns(true);

        var directory = Path.Combine(_root, "training", "base", record.Id.ToString());
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "model.safetensors"), "weights");

        AssertEx.Equal(BaseArtifactDeleteOutcome.Deleted, await Service().DeleteAsync(record.Id, CancellationToken.None));
        AssertEx.False(Directory.Exists(directory), "A deleted artifact must not leave its weights on disk.");
    }

    /// <summary>A Model Lab start on the checkpoint is refused while the delete holds it: from before the worker's eviction through the row delete.</summary>
    [Test]
    public async Task Delete_HoldsTheCheckpointFromBeforeTheWorkerEviction_ThroughTheRowDelete()
    {
        var record = Record(TrainingBaseArtifactStatus.Ready);
        var claim = Substitute.For<IDisposable>();
        _ = _labJob.ClaimForDelete(record.Id).Returns(claim);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);
        var heldAtRowDelete = false;
        _ = _store.DeleteAsync(record.Id, record.Version, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldAtRowDelete = claim.ReceivedCalls().All(static call => call.GetMethodInfo().Name != nameof(IDisposable.Dispose));
            return true;
        });

        AssertEx.Equal(BaseArtifactDeleteOutcome.Deleted, await Service().DeleteAsync(record.Id, CancellationToken.None));

        Received.InOrder(() =>
        {
            _ = _labJob.ClaimForDelete(record.Id);
            _ = _labJob.EvictIdleWorkerAsync(record.Id, Arg.Any<CancellationToken>());
        });
        AssertEx.True(heldAtRowDelete, "The hold was let go before the row delete.");
        claim.Received(1).Dispose();
    }

    [Test]
    [Arguments(ModelLabEjectOutcome.Busy, BaseArtifactDeleteOutcome.InUseByExperiment)]
    [Arguments(ModelLabEjectOutcome.NotConfirmed, BaseArtifactDeleteOutcome.WorkerNotConfirmed)]
    public async Task Delete_RefusedByTheWorker_LetsGoOfTheCheckpoint(ModelLabEjectOutcome eviction, BaseArtifactDeleteOutcome expected)
    {
        var record = Record(TrainingBaseArtifactStatus.Ready);
        var claim = Substitute.For<IDisposable>();
        _ = _labJob.ClaimForDelete(record.Id).Returns(claim);
        _ = _labJob.EvictIdleWorkerAsync(record.Id, Arg.Any<CancellationToken>()).Returns(eviction);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);

        AssertEx.Equal(expected, await Service().DeleteAsync(record.Id, CancellationToken.None));

        claim.Received(1).Dispose();
        await _store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WhoseRowDeleteFails_LetsGoOfTheCheckpoint()
    {
        var record = Record(TrainingBaseArtifactStatus.Ready);
        var claim = Substitute.For<IDisposable>();
        _ = _labJob.ClaimForDelete(record.Id).Returns(claim);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);
        _ = _store.DeleteAsync(record.Id, record.Version, Arg.Any<CancellationToken>()).Returns<bool>(static _ => throw new InvalidOperationException("database is locked"));

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Service().DeleteAsync(record.Id, CancellationToken.None));

        claim.Received(1).Dispose();
    }

    [Test]
    public async Task CancelAsync_WhenNothingIsRunning_ReportsFalse()
    {
        AssertEx.False(await Service().CancelAsync(Guid.NewGuid()));
    }

    private BaseArtifactService Service()
    {
        return new BaseArtifactService(_store,
            _checkpointStore,
            _coordinator,
            _freeSpaceProbe,
            DataDirectory,
            _labJob,
            TimeProvider.System);
    }

    private static BaseCheckpointManifest Manifest(long totalBytes)
    {
        return new BaseCheckpointManifest
        {
            RepoId = RepoId,
            Revision = "main",
            Files =
            [
                new BaseCheckpointFile
                {
                    Role = BaseCheckpointFileRole.Weights,
                    FileName = "model.safetensors",
                    SizeBytes = totalBytes
                }
            ],
            TotalBytes = totalBytes,
            License = "llama3.2",
            IsGated = false
        };
    }

    private static TrainingBaseArtifactRecord Record(TrainingBaseArtifactStatus status, string revision = "main")
    {
        return new TrainingBaseArtifactRecord
        {
            Id = Guid.NewGuid(),
            RepoId = RepoId,
            Revision = revision,
            Status = status,
            FilesJson = ReadOnlyMemory<byte>.Empty,
            TotalBytes = 0,
            LicenseJson = null,
            ErrorMessage = null,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0
        };
    }
}
