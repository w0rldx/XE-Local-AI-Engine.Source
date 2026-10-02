namespace XE_Local_AI_Engine.Tests.ModelFit;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Transcription;

/// <summary>
///     <see cref="RuntimeResidentsService" /> over the real activity gates: the image and whisper state rules, the
///     transcription kill switch, and <c>canEject</c> refusing on every field the two eviction reservations refuse on.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RuntimeResidentsServiceTests
{
    // One per refusal condition of TryAcquireEvictionReservation. The image job lease is taken at enqueue
    // (ImageJobCoordinator.EnqueueAsync), so it stands for a queued job and a running one alike.
    public enum Blocker
    {
        JobOrTranscription,
        SpawnReadiness,
        MutationReservation,
        EvictionReservation
    }

    private readonly ImageRuntimeActivityGate _imageGate = new();
    private readonly IImageServerSupervisor _imageSupervisor = Substitute.For<IImageServerSupervisor>();
    private readonly WhisperRuntimeActivityGate _whisperGate = new();

    private readonly FakeWhisperServerSupervisor _whisperSupervisor = new()
    {
        Status = WhisperStatus(WhisperRuntimeState.Stopped, modelId: null, backend: null)
    };

    [Test]
    public async Task GetResidents_WithNothingResident_IsEmpty()
    {
        _imageSupervisor.GetResidents().Returns([]);

        AssertEx.Equal(expected: 0, (await CreateService().GetResidentsAsync()).Count);
    }

    [Test]
    public async Task ImageRows_FollowTheProcessTable_ExitedThenLeasedThenIdle()
    {
        _imageSupervisor.GetResidents().Returns([
            ImageProcess("idle-model", leased: false, exited: false),
            ImageProcess("busy-model", leased: true, exited: false),
            ImageProcess("dead-model", leased: true, exited: true)
        ]);

        var rows = await CreateService().GetResidentsAsync();

        AssertEx.Equal("idle-model:Idle,busy-model:Active,dead-model:Exited",
            string.Join(',', rows.Select(static row => $"{row.ModelId}:{row.State}")));
        AssertEx.True(rows.All(static row => row is { Runtime: RuntimeResidentKind.Image, Backend: null, CanEject: true }));
    }

    [Test]
    public async Task ImageSpawnInFlight_WithAnEmptyTable_IsOneStartingRowWithoutAModel()
    {
        _imageSupervisor.GetResidents().Returns([]);
        using var spawn = AssertEx.NotNull(_imageGate.TryAcquireSpawnReadinessLease());

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.Equal(RuntimeResidentKind.Image, row.Runtime);
        AssertEx.Equal(RuntimeResidentState.Starting, row.State);
        AssertEx.Null(row.ModelId);
        AssertEx.False(row.CanEject);
    }

    [Test]
    public async Task ImageSpawnInFlight_WithARegisteredDaemon_ListsTheTableOnly()
    {
        _imageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        using var spawn = AssertEx.NotNull(_imageGate.TryAcquireSpawnReadinessLease());

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.Equal("sd15", row.ModelId);
        AssertEx.Equal(RuntimeResidentState.Idle, row.State);
    }

    [Test]
    public async Task Whisper_Stopped_HasNoRow()
    {
        _imageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Stopped, modelId: null, backend: null);

        AssertEx.Equal(RuntimeResidentKind.Image, (await CreateService().GetResidentsAsync()).Single().Runtime);
    }

    [Test]
    public async Task Whisper_Starting_IsAStartingRowWithoutAModel()
    {
        _imageSupervisor.GetResidents().Returns([]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Starting, modelId: null, backend: null);
        using var spawn = AssertEx.NotNull(_whisperGate.TryAcquireSpawnReadinessLease());

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.Equal(RuntimeResidentKind.Transcription, row.Runtime);
        AssertEx.Equal(RuntimeResidentState.Starting, row.State);
        AssertEx.Null(row.ModelId);
        AssertEx.False(row.CanEject);
    }

    [Test]
    public async Task Whisper_ReadyWithNoTranscription_IsIdleAndEjectable()
    {
        _imageSupervisor.GetResidents().Returns([]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "large-v3-turbo", WhisperBackend.Cuda);

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.Equal(RuntimeResidentState.Idle, row.State);
        AssertEx.Equal("large-v3-turbo", row.ModelId);
        AssertEx.Equal(WhisperBackend.Cuda, row.Backend);
        AssertEx.True(row.CanEject);
    }

    [Test]
    public async Task Whisper_ReadyWithATranscriptionInFlight_IsActive()
    {
        _imageSupervisor.GetResidents().Returns([]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "base", WhisperBackend.Cpu);
        using var transcription = AssertEx.NotNull(_whisperGate.TryAcquireTranscriptionLease());

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.Equal(RuntimeResidentState.Active, row.State);
        AssertEx.False(row.CanEject);
    }

    [Test]
    public async Task TranscriptionDisabled_ReportsNoWhisperRow_ButKeepsImageRows()
    {
        _imageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "base", WhisperBackend.Cpu);

        var rows = await CreateService(transcriptionEnabled: false).GetResidentsAsync();

        AssertEx.Equal(RuntimeResidentKind.Image, rows.Single().Runtime);
    }

    [Test]
    [Arguments(Blocker.JobOrTranscription)]
    [Arguments(Blocker.SpawnReadiness)]
    [Arguments(Blocker.MutationReservation)]
    [Arguments(Blocker.EvictionReservation)]
    public async Task ImageCanEject_IsFalseWhileTheGateWouldRefuseTheEviction(Blocker blocker)
    {
        _imageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        using var lease = AssertEx.NotNull(blocker switch
        {
            Blocker.JobOrTranscription => _imageGate.TryAcquireJobLease(),
            Blocker.SpawnReadiness => _imageGate.TryAcquireSpawnReadinessLease(),
            Blocker.MutationReservation => _imageGate.TryAcquireMutationReservation(),
            _ => _imageGate.TryAcquireEvictionReservation()
        });

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.False(row.CanEject, $"{blocker} must block the image eject.");
        AssertEx.Null(_imageGate.TryAcquireEvictionReservation());
    }

    [Test]
    [Arguments(Blocker.JobOrTranscription)]
    [Arguments(Blocker.SpawnReadiness)]
    [Arguments(Blocker.MutationReservation)]
    [Arguments(Blocker.EvictionReservation)]
    public async Task WhisperCanEject_IsFalseWhileTheGateWouldRefuseTheEviction(Blocker blocker)
    {
        _imageSupervisor.GetResidents().Returns([]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "base", WhisperBackend.Cpu);
        using var lease = AssertEx.NotNull(blocker switch
        {
            Blocker.JobOrTranscription => _whisperGate.TryAcquireTranscriptionLease(),
            Blocker.SpawnReadiness => _whisperGate.TryAcquireSpawnReadinessLease(),
            Blocker.MutationReservation => _whisperGate.TryAcquireMutationReservation(),
            _ => _whisperGate.TryAcquireEvictionReservation()
        });

        var row = (await CreateService().GetResidentsAsync()).Single();

        AssertEx.False(row.CanEject, $"{blocker} must block the transcription eject.");
        AssertEx.Null(_whisperGate.TryAcquireEvictionReservation());
    }

    [Test]
    public async Task CanEject_IsTrueWhileResidentLeasesAloneAreHeld()
    {
        // A resident daemon is exactly what an eject is for, so neither eviction reservation refuses on it.
        _imageSupervisor.GetResidents().Returns([ImageProcess("sd15", leased: false, exited: false)]);
        _whisperSupervisor.Status = WhisperStatus(WhisperRuntimeState.Ready, "base", WhisperBackend.Cpu);
        using var imageResident = AssertEx.NotNull(_imageGate.TryAcquireResidentProcessLease());
        using var whisperResident = AssertEx.NotNull(_whisperGate.TryAcquireResidentProcessLease());

        var rows = await CreateService().GetResidentsAsync();

        AssertEx.Equal(expected: 2, rows.Count);
        AssertEx.True(rows.All(static row => row.CanEject));
    }

    private RuntimeResidentsService CreateService(bool transcriptionEnabled = true)
    {
        return new RuntimeResidentsService(_imageSupervisor,
            _imageGate,
            _whisperSupervisor,
            _whisperGate,
            SeededNodeRuntimeSettings.FromSeed("Transcription:Enabled", transcriptionEnabled));
    }

    private static ImageServerResidentSnapshot ImageProcess(string modelName, bool leased, bool exited)
    {
        return new ImageServerResidentSnapshot
        {
            ModelName = modelName,
            HasActiveJobLease = leased,
            HasExited = exited,
            LastUsedUtc = DateTimeOffset.UnixEpoch
        };
    }

    private static WhisperRuntimeStatusSnapshot WhisperStatus(WhisperRuntimeState state, string? modelId, WhisperBackend? backend)
    {
        return new WhisperRuntimeStatusSnapshot
        {
            State = state,
            LoadedModelId = modelId,
            Backend = backend,
            BinaryVersion = null,
            BinarySource = null,
            SupportsTranscode = false
        };
    }
}
