namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The image and whisper daemons and the warm Model Lab worker held in memory, for the top-bar widget, which refetches on the
///     residency hub's tick. llama.cpp rows stay on <see cref="LlamaCppRuntimeOrchestrationService" />.
/// </summary>
/// <remarks>
///     Reads the two supervisors' in-memory state, the two activity gates, the warm worker's source and the cached transcription switch
///     only: no disk, no probe, no recommendation, which is why it does not go through <see cref="ITranscriptionRuntimeService" />.
///     <see cref="RuntimeResident.CanEject" /> repeats each gate's <c>TryAcquireEvictionReservation</c> refusal (Model Lab: Idle only), so
///     the flag and the eject endpoint agree; the rows and that flag come from separate reads and can briefly disagree.
/// </remarks>
public sealed class RuntimeResidentsService
{
    private readonly IImageRuntimeActivityGate _imageGate;
    private readonly IImageServerSupervisor _imageSupervisor;
    private readonly IModelLabWarmResidentSource _modelLabWarm;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IWhisperRuntimeActivityGate _whisperGate;
    private readonly IWhisperServerSupervisor _whisperSupervisor;

    public RuntimeResidentsService(IImageServerSupervisor imageSupervisor,
        IImageRuntimeActivityGate imageGate,
        IWhisperServerSupervisor whisperSupervisor,
        IWhisperRuntimeActivityGate whisperGate,
        INodeRuntimeSettings runtimeSettings,
        IModelLabWarmResidentSource modelLabWarm)
    {
        ArgumentNullException.ThrowIfNull(imageGate);
        ArgumentNullException.ThrowIfNull(imageSupervisor);
        ArgumentNullException.ThrowIfNull(modelLabWarm);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(whisperGate);
        ArgumentNullException.ThrowIfNull(whisperSupervisor);
        _imageGate = imageGate;
        _imageSupervisor = imageSupervisor;
        _modelLabWarm = modelLabWarm;
        _runtimeSettings = runtimeSettings;
        _whisperGate = whisperGate;
        _whisperSupervisor = whisperSupervisor;
    }

    public async Task<IReadOnlyList<RuntimeResident>> GetResidentsAsync(CancellationToken cancellationToken = default)
    {
        var residents = new List<RuntimeResident>();
        AddImageResidents(residents);

        // The switch FeatureSwitchMiddleware reads for /transcription; a disabled node reports no whisper rows.
        if (await _runtimeSettings.GetTranscriptionEnabledAsync(cancellationToken))
        {
            AddTranscriptionResident(residents);
        }

        if (_modelLabWarm.GetResident() is { } warm)
        {
            residents.Add(new RuntimeResident
            {
                Runtime = RuntimeResidentKind.ModelLab,
                ModelId = warm.ModelId,
                State = warm.State,
                Backend = null,
                CanEject = warm.State == RuntimeResidentState.Idle,
                VramBytes = warm.VramBytes
            });
        }

        return residents;
    }

    private void AddImageResidents(List<RuntimeResident> residents)
    {
        var activity = _imageGate.GetSnapshot();
        var canEject = !(activity.MutationReserved || activity.EvictionReserved || activity.ActiveJobCount != 0 || activity.SpawnReadinessCount != 0);
        var processes = _imageSupervisor.GetResidents();
        if (processes.Count == 0 && activity.SpawnReadinessCount > 0)
        {
            residents.Add(ImageResident(modelId: null, RuntimeResidentState.Starting, canEject));
            return;
        }

        foreach (var process in processes)
        {
            var state = process switch
            {
                { HasExited: true } => RuntimeResidentState.Exited,
                { HasActiveJobLease: true } => RuntimeResidentState.Active,
                _ => RuntimeResidentState.Idle
            };
            residents.Add(ImageResident(process.ModelName, state, canEject));
        }
    }

    private void AddTranscriptionResident(List<RuntimeResident> residents)
    {
        var status = _whisperSupervisor.GetStatus();
        if (status.State == WhisperRuntimeState.Stopped)
        {
            return;
        }

        var activity = _whisperGate.GetSnapshot();
        var state = (status.State, activity.ActiveJobCount) switch
        {
            (WhisperRuntimeState.Starting, _) => RuntimeResidentState.Starting,
            (_, > 0) => RuntimeResidentState.Active,
            _ => RuntimeResidentState.Idle
        };
        residents.Add(new RuntimeResident
        {
            Runtime = RuntimeResidentKind.Transcription,
            ModelId = status.LoadedModelId,
            State = state,
            Backend = status.Backend,
            CanEject = !(activity.MutationReserved || activity.EvictionReserved || activity.ActiveJobCount != 0 || activity.SpawnReadinessCount != 0)
        });
    }

    private static RuntimeResident ImageResident(string? modelId, RuntimeResidentState state, bool canEject)
    {
        return new RuntimeResident
        {
            Runtime = RuntimeResidentKind.Image,
            ModelId = modelId,
            State = state,
            Backend = null,
            CanEject = canEject
        };
    }
}
