namespace XE_Local_AI_Engine.Tests.Providers.Training;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the orchestration guards around base-checkpoint acquisition: the disk preflight, the delete guard while a
///     transfer is live, and the cancel path.
/// </summary>
public sealed class BaseArtifactServiceTests : IDisposable
{
    private const string RepoId = "unsloth/Llama-3.2-1B-Instruct";

    private readonly BaseArtifactDownloadCoordinator _coordinator;
    private readonly IBaseCheckpointStore _checkpointStore = Substitute.For<IBaseCheckpointStore>();
    private readonly IFreeSpaceProbe _freeSpaceProbe = Substitute.For<IFreeSpaceProbe>();
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

    [Test]
    public async Task Delete_WhileTheDownloadIsStillRunning_IsRefused()
    {
        var record = Record(TrainingBaseArtifactStatus.Downloading);
        _ = _store.GetAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);

        var outcome = await Service().DeleteAsync(record.Id, CancellationToken.None);

        AssertEx.Equal(BaseArtifactDeleteOutcome.Downloading, outcome);
        await _store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WhenTheArtifactDoesNotExist_ReportsNotFound()
    {
        _ = _store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TrainingBaseArtifactRecord?)null);

        AssertEx.Equal(BaseArtifactDeleteOutcome.NotFound, await Service().DeleteAsync(Guid.NewGuid(), CancellationToken.None));
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

    [Test]
    public void Cancel_WhenNothingIsRunning_ReportsFalse()
    {
        AssertEx.False(Service().Cancel(Guid.NewGuid()));
    }

    private BaseArtifactService Service()
    {
        return new BaseArtifactService(_store,
            _checkpointStore,
            _coordinator,
            _freeSpaceProbe,
            DataDirectory,
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

    private static TrainingBaseArtifactRecord Record(TrainingBaseArtifactStatus status)
    {
        return new TrainingBaseArtifactRecord(Guid.NewGuid(),
            RepoId,
            "main",
            status,
            ReadOnlyMemory<byte>.Empty,
            TotalBytes: 0,
            LicenseJson: null,
            ErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }
}
