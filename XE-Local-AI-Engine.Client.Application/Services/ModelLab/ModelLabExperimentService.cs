namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Admits, runs and records Model Lab's trace jobs: one at a time, refused when busy, with no queue.</summary>
/// <remarks>
///     A cold start takes the exclusive gate, the runtime-mutation lease (null while a model is loaded: refused, no eject) and the byte
///     reservation, and hands them to a <see cref="ModelLabGpuSession" /> whose serving worker may stay warm for the next start on the same
///     checkpoint. A warm start touches none of the three. Prompts and vectors reach disk only in the owner-only work directory, deleted on
///     every path and at startup. A steering job stops its worker after the derive and keeps the gate and the lease for its arms.
/// </remarks>
public sealed class ModelLabExperimentService : IHostedService, IModelLabActiveJob, IModelLabWarmResidentSource
{
    public const int MaxRetainedExperiments = 20;

    public const int MaxPinnedExperiments = 20;

    public const int MaxNotesCharacters = 8000;

    private const int MaxAppVersionCharacters = 64;

    private const string TraceFileName = "trace.jsonl";
    private const string AttentionFileName = "attention.jsonl";
    private const string ResultFileName = "result.json";
    private const string VectorFileName = "steer.gguf";
    private const string RandomVectorFileName = "random.gguf";
    private const string JobFileName = "job.json";

    internal const string RuntimeChangingMessage = "The Python training runtime is being installed or removed. Wait for it to finish, then start the experiment again.";

    private const string StoppingMessage = "The node is stopping. Start the experiment again once it is back.";

    private const string CheckpointNotFoundMessage = "The checkpoint was not found. Download it first.";

    /// <summary>One F32 value per hidden dimension: about 10 kB at a hidden size of 2560, so this bounds a vector for any model that fits a card.</summary>
    private const int MaxVectorBytes = 256 * 1024;

    private readonly ModelLabArmRunner _arms;
    private readonly ManagedEncryptedBlobStore _blobs;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ITrainingRunEventBuffer _events;
    private readonly GpuIdleHolderBroker _broker;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly ITrainingProcessInspector _inspector;
    private readonly IJobCreatedPublisher _jobCreated;
    private readonly Lock _jobLock = new();
    private readonly ILogger<ModelLabExperimentService> _logger;
    private readonly ModelLabJobOptions _options;
    private readonly ModelLabCheckpointPreflightService _preflight;
    private readonly IRuntimeResidencyChangeNotifier _residency;
    private readonly ITrainingRuntimeService _runtime;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeRuntimeSettings _settings;
    private readonly ITrainingProcessSpawner _spawner;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Receipt files not proven gone, with what an unverified eviction kept for each (nothing for the startup sweep's). Under the job lock.</summary>
    private readonly Dictionary<string, UnresolvedReceipt> _unresolvedReceipts = new(StringComparer.Ordinal);

    /// <summary>Checkpoints a delete holds, with how many deletes hold each. Under the job lock.</summary>
    private readonly Dictionary<Guid, int> _deleteClaims = [];

    private ActiveJob? _active;
    private bool _receiptScanFailed;
    private ModelLabGpuSession? _session;

    /// <summary>Set under the job lock by a host stop; from then on no start claims or publishes a session.</summary>
    private bool _stopping;

    public ModelLabExperimentService(IServiceScopeFactory scopeFactory,
        ModelLabCheckpointPreflightService preflight,
        IGpuWorkGate gpuWorkGate,
        ILlamaServerProcessSupervisor supervisor,
        ITrainingRuntimeService runtime,
        ITrainingProcessSpawner spawner,
        ITrainingProcessInspector inspector,
        ITrainingRunEventBuffer events,
        ModelLabArmRunner arms,
        GpuIdleHolderBroker broker,
        IRuntimeResidencyChangeNotifier residency,
        INodeRuntimeSettings settings,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        IOptions<ModelLabJobOptions> options,
        IJobCreatedPublisher jobCreated,
        TimeProvider timeProvider,
        ILogger<ModelLabExperimentService> logger)
    {
        ArgumentNullException.ThrowIfNull(jobCreated);
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(residency);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _arms = arms;
        _blobs = new ManagedEncryptedBlobStore(dataDirectory, keyHolder, "model-lab", "artifacts", "model_lab_artifact_blob",
            Math.Max(ModelLabTraceSpec.MaxTraceByteCap, ModelLabTraceSpec.MaxAttentionByteCap), "Model Lab artifact");
        _broker = broker;
        _dataDirectory = dataDirectory;
        _events = events;
        _gpuWorkGate = gpuWorkGate;
        _inspector = inspector;
        _jobCreated = jobCreated;
        _logger = logger;
        _options = options.Value;
        _preflight = preflight;
        _residency = residency;
        _runtime = runtime;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _spawner = spawner;
        _supervisor = supervisor;
        _timeProvider = timeProvider;
        IdleClock = timeProvider;
    }

    /// <summary>Times a warm worker's idle TTL. A test swaps in a clock it advances, before the session it times is created.</summary>
    internal TimeProvider IdleClock { get; set; }

    /// <summary>The job is Linux only. A test swaps this to see the other platforms' answer.</summary>
    internal Func<bool> PlatformProbe { get; set; } = OperatingSystem.IsLinux;

    public bool IsPlatformSupported => PlatformProbe();

    public static string UnsupportedPlatformMessage =>
        "Tracing runs on Linux only for now, because it needs the Python training runtime, which is available on Linux only. The other Model Lab tools work here.";

    /// <summary>Runs just before a trace's attention blob is written. A test throws here to fail the second write after the first has landed.</summary>
    internal Action? BeforeAttentionBlobWrite { get; set; }

    /// <summary>Awaited by tests so a started job can be observed to its end; null when nothing has run.</summary>
    internal Task? InFlight { get; private set; }

    /// <summary>The running job, from the moment its row exists: the hub refuses an id with no row, and the page does not retry.</summary>
    public Guid? ActiveExperimentId
    {
        get
        {
            lock (_jobLock)
            {
                return _active is { RowCommitted: true } job ? job.ExperimentId : null;
            }
        }
    }

    /// <summary>The running job's checkpoint, or a session's still borrowed by a job: an ending job lets go of its slot before its session leaves Active.</summary>
    public Guid? ActiveBaseArtifactId
    {
        get
        {
            lock (_jobLock)
            {
                return _active?.BaseArtifactId
                       ?? (_session is { State: ModelLabSessionState.Admitting or ModelLabSessionState.Active } session ? session.BaseArtifactId : null);
            }
        }
    }

    /// <summary>The node's informational version, recorded on each run so a comparison can show it.</summary>
    internal static string? AppVersion { get; } =
        typeof(ModelLabExperimentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
            ? version[..Math.Min(version.Length, MaxAppVersionCharacters)]
            : null;

    /// <summary>The fit and pairing preflight, then the license gate, then the download. A refused pick fetches no weights.</summary>
    public async Task<ModelLabCheckpointDownload> StartCheckpointDownloadAsync(string repoId,
        string? revision,
        string installedModelName,
        bool licenseConfirmed,
        CancellationToken ct)
    {
        if (!IsPlatformSupported)
        {
            return new ModelLabCheckpointDownload
            {
                Preflight = new ModelLabCheckpointPreflight
                {
                    RepoId = repoId,
                    Refusal = ModelLabCheckpointRefusal.UnsupportedPlatform,
                    RefusalMessage = UnsupportedPlatformMessage
                }
            };
        }

        var preflight = await _preflight.CheckAsync(repoId, revision, installedModelName, ct);
        if (preflight.Refusal is not null)
        {
            return new ModelLabCheckpointDownload
            {
                Preflight = preflight
            };
        }

        if (!licenseConfirmed)
        {
            return new ModelLabCheckpointDownload
            {
                Preflight = preflight with
                {
                    Refusal = ModelLabCheckpointRefusal.LicenseNotConfirmed,
                    RefusalMessage = preflight.LicenseConfirmationText
                }
            };
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var artifact = await scope.ServiceProvider.GetRequiredService<IBaseArtifactService>().StartDownloadAsync(preflight.RepoId, preflight.Revision, ct);
            return new ModelLabCheckpointDownload
            {
                Preflight = preflight,
                BaseArtifactId = artifact.Id,
                ArtifactStatus = artifact.Status
            };
        }
        catch (BaseArtifactRejectedException exception)
        {
            return new ModelLabCheckpointDownload
            {
                Preflight = preflight with
                {
                    Refusal = ModelLabCheckpointRefusal.DownloadRejected,
                    RefusalMessage = exception.Message
                }
            };
        }
    }

    public async Task<ModelLabStart> StartExperimentAsync(ModelLabExperimentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsPlatformSupported)
        {
            return Refuse(ModelLabStartRefusal.UnsupportedPlatform, UnsupportedPlatformMessage);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        BaseArtifactView artifact;
        try
        {
            artifact = await services.GetRequiredService<IBaseArtifactService>().GetAsync(request.BaseArtifactId, ct);
        }
        catch (TrainingNotFoundException)
        {
            return Refuse(ModelLabStartRefusal.CheckpointNotFound, CheckpointNotFoundMessage);
        }

        if (artifact.Status != nameof(TrainingBaseArtifactStatus.Ready))
        {
            return Refuse(ModelLabStartRefusal.CheckpointNotReady, "The checkpoint has not finished downloading.");
        }

        if (!request.LicenseConfirmed)
        {
            return Refuse(ModelLabStartRefusal.LicenseNotConfirmed, "Confirm the checkpoint's license first.");
        }

        var preflight = await _preflight.CheckDownloadedAsync(artifact, request.InstalledModelName, ct);
        if (preflight.Refusal is not null || preflight.CheckpointBlockCount is not { } blockCount)
        {
            return Refuse(ModelLabStartRefusal.PreflightRefused, preflight.RefusalMessage ?? "The checkpoint could not be checked.");
        }

        var scriptPath = Path.Combine(TrainingScripts.ResolveDirectory(), TrainingScripts.ModelLabScriptName);
        var phase = _runtime.GetStatus().Phase;
        if (phase is TrainingRuntimePhase.AcquiringUv or TrainingRuntimePhase.ProvisioningPython or TrainingRuntimePhase.InstallingPackages
            or TrainingRuntimePhase.Verifying or TrainingRuntimePhase.Removing)
        {
            return Refuse(ModelLabStartRefusal.RuntimeUnavailable, RuntimeChangingMessage);
        }

        if (_runtime.ResolveInterpreterPath() is not { } interpreter
            || phase != TrainingRuntimePhase.Ready
            || !File.Exists(scriptPath))
        {
            return Refuse(ModelLabStartRefusal.RuntimeUnavailable, "The Python training runtime is not installed. Install it on the Training page, then start the trace again.");
        }

        if (request.Steering is null)
        {
            // The range depends on the checkpoint, and a layer past it would fail only after the model has loaded. The rest repeats the validator's bounds the cap relies on.
            if (request.Layers is { } layers
                && (layers.Count is 0 or > ModelLabTraceSpec.MaxTraceLayers || layers.Distinct().Count() != layers.Count || layers.Any(traced => traced < 0 || traced >= blockCount)))
            {
                return Refuse(ModelLabStartRefusal.InvalidTraceLayers, string.Create(CultureInfo.InvariantCulture,
                    $"This checkpoint has blocks 0 to {blockCount - 1}. Choose distinct trace layers in that range."));
            }

            if (request.RecordAttention && AttentionRefusalOrNull(preflight) is { } noAttention)
            {
                return Refuse(ModelLabStartRefusal.AttentionUnavailable, noAttention);
            }

            // The validator's bound, which the job's byte caps rely on; past it the caps would throw once the holds are taken.
            if (request.MaxNewTokens is < 1 or > ModelLabTraceSpec.MaxRequestedNewTokens)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.MaxNewTokens, "A trace generates 1 to 256 tokens.");
            }

            return await AdmitAndStartAsync(request, artifact, preflight, layer: null, interpreter, scriptPath, services, ct);
        }

        // Decided here because the range depends on the model, and before the gate because a bad layer must not cost a derive.
        // llama-server drops a vector outside this range without an error, so the harness refuses it too.
        var steerableBlocks = (int)(preflight.GgufSteerableBlockCount ?? blockCount);
        var layer = request.Steering.Layer ?? preflight.DefaultSteeringLayer ?? 0;
        if (layer < 1 || layer >= steerableBlocks)
        {
            return Refuse(ModelLabStartRefusal.InvalidLayer, string.Create(CultureInfo.InvariantCulture,
                $"This model can be steered only at layers 1 to {steerableBlocks - 1}. Block 0 has no vector slot, and a layer past the model's trunk would be dropped silently."));
        }

        // The first arm's server would be told the same, after the derive has spent its GPU time. A trace starts no server.
        if (await GetRuntimeRefusalAsync(services, ct) is { } noRuntime)
        {
            return Refuse(ModelLabStartRefusal.RuntimeUnavailable, noRuntime);
        }

        return await AdmitAndStartAsync(request, artifact, preflight, layer, interpreter, scriptPath, services, ct);
    }

    /// <summary>The cap is computed from the checkpoint's own shape, so a config that does not state it cannot record attention.</summary>
    private static string? AttentionRefusalOrNull(ModelLabCheckpointPreflight preflight)
    {
        if (preflight.CheckpointLayerTypes is not { } layerTypes || preflight.CheckpointAttentionHeads is not { } heads)
        {
            return "This checkpoint's config does not state its block kinds and attention heads, so attention cannot be recorded. Start the trace without it.";
        }

        if (ModelLabTraceSpec.AttentionLayersFor(layerTypes).Count == 0)
        {
            return "This checkpoint has no full-attention block, and only those have attention weights to record. Start the trace without it.";
        }

        return heads is < 1 or > ModelLabTraceSpec.MaxAttentionHeads
            ? string.Create(CultureInfo.InvariantCulture,
                $"This checkpoint has {heads} attention heads per block, and a recording holds 1 to {ModelLabTraceSpec.MaxAttentionHeads}. Start the trace without it.")
            : null;
    }

    private static async Task<string?> GetRuntimeRefusalAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var variant = await services.GetRequiredService<IGpuVariantSelector>().SelectVariantAsync(ct);
            return await services.GetRequiredService<ILlamaCppBinaryManager>().GetImplicitAcquisitionRefusalAsync(variant, ct);
        }
        catch (LlamaRuntimeException exception)
        {
            return exception.Message;
        }
    }

    public bool Cancel(Guid experimentId)
    {
        lock (_jobLock)
        {
            if (_active?.ExperimentId != experimentId)
            {
                return false;
            }

            _active.Cancellation.Cancel();
            return true;
        }
    }

    public async Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().ListAsync(ct);
    }

    public async Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().GetAsync(id, ct);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().ExistsAsync(id, ct);
    }

    public async Task<ModelLabExperimentDeleteOutcome> DeleteAsync(Guid id, CancellationToken ct)
    {
        // The live job is the rule, not the row's status: a failed completion leaves a Running row with no job behind it.
        lock (_jobLock)
        {
            if (_active?.ExperimentId == id)
            {
                return ModelLabExperimentDeleteOutcome.Running;
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().DeleteAsync(id, ct);
        if (outcome == ModelLabExperimentDeleteOutcome.Deleted)
        {
            _blobs.DeleteScope(id);
        }

        return outcome;
    }

    public async Task<ModelLabExperimentUpdateOutcome> SetNotesAsync(Guid id, string? notes, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetNotesAsync(id, notes, ct);
    }

    public async Task<ModelLabExperimentUpdateOutcome> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetPinnedAsync(id, pinned, MaxPinnedExperiments, ct);
    }

    public async Task<ModelLabTrace> ReadTraceAsync(Guid id, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        // A steering row's blob is its vector, which has no route out of this node.
        if (record is not { Kind: ModelLabExperimentKind.Trace, TraceBlobId: { } blobId, TraceSha256: { } hash, TraceByteCount: { } bytes })
        {
            return new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        return read.Status == ManagedBlobReadStatus.Found
            ? ModelLabTraceSpec.ParseTrace(read.Content)
            : new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    /// <summary>One captured block's rows. Not found for a steering row, a trace that recorded no attention, or a block it did not capture.</summary>
    public async Task<ModelLabAttention> ReadAttentionAsync(Guid id, int layer, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        if (record is not { Kind: ModelLabExperimentKind.Trace, AttentionBlobId: { } blobId, AttentionSha256: { } hash, AttentionByteCount: { } bytes })
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        // The blob's shape is the result's: without a readable summary its rows cannot be decoded.
        if ((record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json)?.Attention : null) is not { Heads: > 0, WeightScale: > 0 } summary)
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        }

        if (!summary.Layers.Contains(layer))
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        var attention = read.Status == ManagedBlobReadStatus.Found
            ? ModelLabTraceSpec.ParseAttention(read.Content, summary.Heads)
            : new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        return attention.Status == ModelLabTraceReadStatus.Found
            ? attention with
            {
                Summary = summary,
                Records = [.. attention.Records.Where(row => row.Layer == layer)]
            }
            : attention;
    }

    /// <summary>The trace's logit lens. Not found for a steering row or a trace that recorded none: one saved before contract 5, or of a checkpoint too deep for it.</summary>
    public async Task<ModelLabLens> ReadLensAsync(Guid id, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        if (record is not { Kind: ModelLabExperimentKind.Trace, TraceBlobId: { } blobId, TraceSha256: { } hash, TraceByteCount: { } bytes })
        {
            return new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        // The lines are checked against the run's own summary and token count, so without a readable result they cannot be.
        if ((record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json) : null) is not { } result)
        {
            return new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        }

        if (result.Lens is not { } summary)
        {
            return new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        return read.Status == ManagedBlobReadStatus.Found
            ? ModelLabTraceSpec.ParseLens(read.Content, summary, result.Tokens.Count)
            : new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Host shutdown: the job is cancelled like an operator's cancel and awaited, and a warm worker evicted, so no script outlives a clean stop.</summary>
    /// <remarks>A start claims a session and publishes its job under the same lock that sets the stop, so the read here sees every start that claimed one.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveJob? active;
        Task? last;
        lock (_jobLock)
        {
            _stopping = true;
            active = _active;
            last = InFlight;
        }

        CurrentSession()?.ForbidReuse();
        if (active is not null)
        {
            try
            {
                await active.Cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The job finished between the read and the cancel.
            }

            // A start still in admission ends its waits on this cancel and then says whether it left a job. The job writes its row while the host's scope factory still works.
            if (await active.Admission.Task.WaitAsync(cancellationToken) is { } job)
            {
                await job.WaitAsync(cancellationToken);
            }
        }

        // A job that has let go of its slot may still be handing its session on; its task ends only once that has settled.
        if (last is not null)
        {
            await last.WaitAsync(cancellationToken);
        }

        // Read again: the start awaited above may have admitted a session of its own.
        if (CurrentSession() is { } session)
        {
            session.ForbidReuse();
            _ = await session.EvictAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public ModelLabWarmResident? GetResident() =>
        CurrentSession()?.Resident;

    /// <inheritdoc />
    public async Task<ModelLabEjectOutcome> EjectAsync(CancellationToken cancellationToken)
    {
        if (CurrentSession() is { } session && !await session.EvictAsync(cancellationToken))
        {
            return ModelLabEjectOutcome.Busy;
        }

        // An unseen exit hands its receipt over before the session is released, so the set already names it here.
        return await OrphanRefusalOrNullAsync(cancellationToken) is null ? ModelLabEjectOutcome.Gone : ModelLabEjectOutcome.NotConfirmed;
    }

    /// <inheritdoc />
    public async Task<ModelLabEjectOutcome> EvictIdleWorkerAsync(Guid baseArtifactId, CancellationToken ct)
    {
        if (CurrentSession() is { } session && session.BaseArtifactId == baseArtifactId && !await session.EvictAsync(ct))
        {
            return ModelLabEjectOutcome.Busy;
        }

        _ = await OrphanRefusalOrNullAsync(ct);
        lock (_jobLock)
        {
            // A receipt the startup sweep kept, or one it could not list, names no checkpoint, so it may be this one.
            return _receiptScanFailed || _unresolvedReceipts.Values.Any(unresolved => unresolved.BaseArtifactId is null || unresolved.BaseArtifactId == baseArtifactId)
                ? ModelLabEjectOutcome.NotConfirmed
                : ModelLabEjectOutcome.Gone;
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasUnconfirmedWorkerAsync(CancellationToken ct) =>
        await OrphanRefusalOrNullAsync(ct) is not null;

    /// <inheritdoc />
    public IDisposable ClaimForDelete(Guid baseArtifactId)
    {
        lock (_jobLock)
        {
            _deleteClaims[baseArtifactId] = _deleteClaims.GetValueOrDefault(baseArtifactId) + 1;
        }

        return new DeleteClaim(this, baseArtifactId);
    }

    private void ReleaseDeleteClaim(Guid baseArtifactId)
    {
        lock (_jobLock)
        {
            if (_deleteClaims[baseArtifactId] == 1)
            {
                _deleteClaims.Remove(baseArtifactId);
            }
            else
            {
                _deleteClaims[baseArtifactId]--;
            }
        }
    }

    private ModelLabGpuSession? CurrentSession()
    {
        lock (_jobLock)
        {
            return _session;
        }
    }

    /// <summary>What a serving worker holds: the artifact at its pinned revision, in bf16, under this contract.</summary>
    private static string ModelKeyFor(BaseArtifactView artifact) =>
        string.Create(CultureInfo.InvariantCulture, $"{artifact.Id:N}@{artifact.Revision}/bf16/{ModelLabTraceSpec.ContractVersion}");

    private async Task<ModelLabStart> AdmitAndStartAsync(ModelLabExperimentRequest request,
        BaseArtifactView artifact,
        ModelLabCheckpointPreflight preflight,
        int? layer,
        string interpreter,
        string scriptPath,
        IServiceProvider services,
        CancellationToken ct)
    {
        var modelKey = ModelKeyFor(artifact);
        var id = Guid.NewGuid();
        ActiveJob? active = null;
        ModelLabGpuSession? current;
        lock (_jobLock)
        {
            if (_stopping)
            {
                return Refuse(ModelLabStartRefusal.Busy, StoppingMessage);
            }

            // Before the claim below, so a refusal leaves a warm session exactly as it was.
            if (_deleteClaims.ContainsKey(artifact.Id))
            {
                return Refuse(ModelLabStartRefusal.CheckpointNotFound, CheckpointNotFoundMessage);
            }

            // Before the gate: a warm start takes nothing the session does not already hold, so it can neither be refused nor wait on itself.
            // Claimed and published under the stop's lock: a host stop sees this job, or the claim never happens.
            current = _session;
            if (current?.TryEnterActive(modelKey) == true)
            {
                active = NewJob(id, artifact.Id, current, layer);
                _active = active;
            }
        }

        var warm = active is not null;
        ModelLabGpuSession session;
        IDisposable? admission = null;
        if (active is not null)
        {
            session = active.Session;
            _residency.NotifyChanged();
        }
        else
        {
            // An idle worker on another checkpoint, or one already leaving, goes first. One running a job stays, and the gate refuses below.
            if (current is not null)
            {
                _ = await current.EvictAsync(ct);
            }

            if (await OrphanRefusalOrNullAsync(ct) is { } orphan)
            {
                return orphan;
            }

            session = new ModelLabGpuSession(modelKey, artifact.Id, artifact.RepoId, new ModelLabGpuSessionServices
            {
                Broker = _broker,
                Residency = _residency,
                IdleClock = IdleClock,
                TimeProvider = _timeProvider,
                Logger = _logger,
                LlamaGateHasWaiters = _supervisor.HasRuntimeGateWaiters
            }, (receiptPath, receipt, reservation) => OnOrphaned(receiptPath, receipt, reservation, artifact.Id));
            // Registered before the gate, so demand racing this admission is latched and served when the job ends.
            session.Register();
#pragma warning disable CA2000 // Disposed on every refusal below, otherwise owned by the session.
            if (!_gpuWorkGate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out admission))
#pragma warning restore CA2000
            {
                session.Abandon();
                return Refuse(ModelLabStartRefusal.Busy, "The GPU is busy with other work. Wait for it to finish, or cancel it, and start the experiment again.");
            }

            // The job is held from here, before the lease wait and the row, so a delete in the wait is refused. Like the warm claim, never once
            // stopping, and never while a delete holds the checkpoint: the delete either sees this job or this start sees the delete.
            var refused = Refuse(ModelLabStartRefusal.Busy, StoppingMessage);
            lock (_jobLock)
            {
                if (!_stopping && _deleteClaims.ContainsKey(artifact.Id))
                {
                    refused = Refuse(ModelLabStartRefusal.CheckpointNotFound, CheckpointNotFoundMessage);
                }
                else if (!_stopping)
                {
                    _session = session;
                    active = NewJob(id, artifact.Id, session, layer);
                    _active = active;
                }
            }

            if (active is null)
            {
                admission.Dispose();
                session.Abandon();
                return refused;
            }

            // Only now can a residents read find the Starting row.
            _residency.NotifyChanged();
        }

        ILlamaServerRuntimeMutationLease? lease = null;
        TrainingCapacityReservation? reservation = null;
        var admitted = warm;
        Task? job = null;
        try
        {
            // Again under the gate: a job's unverified eviction can hand its receipt over between the check above and the gate coming free.
            if (!warm && await OrphanRefusalOrNullAsync(ct) is { } orphaned)
            {
                return orphaned;
            }

            // The delete guard could not see this job until _active was set, so the first read may be stale.
            if (await RereadRefusalOrNullAsync(services, artifact.Id, ct) is { } gone)
            {
                return gone;
            }

            // For the two waits only, which a host stop must not sit out. From the row's commit on, a cancel is the job's to see at its first step.
            using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Cancellation.Token);
            if (!warm)
            {
                // Raises no demand: this admission's own session is registered and would otherwise preempt itself.
                lease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(waits.Token, raisesDemand: false);
                if (lease is null)
                {
                    return Refuse(ModelLabStartRefusal.ModelLoaded, "A model is loaded. Eject it, then start the experiment again.");
                }
            }

            // Read again now the lease is held: a runtime install or remove publishes its phase first and then sees this lease as a running job,
            // so between the two reads exactly one of them proceeds. A warm session has held the lease throughout.
            if (_runtime.GetStatus().Phase != TrainingRuntimePhase.Ready)
            {
                return Refuse(ModelLabStartRefusal.RuntimeUnavailable, RuntimeChangingMessage);
            }

            if (!warm)
            {
                reservation = await services.GetRequiredService<ITrainingCapacityGate>().ReserveAsync(new TrainingFootprintEstimate
                {
                    GpuBytes = preflight.RequiredVramBytes ?? 0,
                    RamBytes = ModelLabCheckpointPreflightService.WorkingMemoryBytes,
                    ParameterCount = 0,
                    TrainableParameterCount = 0,
                    Experimental = false
                }, waits.Token);
                if (!reservation.Granted)
                {
                    return Refuse(ModelLabStartRefusal.InsufficientMemory, reservation.Reason ?? "There is not enough free memory to start the experiment.");
                }

                session.Admitted(admission!, lease!, reservation);
                admitted = true;
            }

            var license = services.GetRequiredService<ILicenseGateService>();
            await CreateRowAsync(services, id, request, artifact, license, ct);
            lock (_jobLock)
            {
                active.RowCommitted = true;
            }

            var checkpoint = BaseArtifactManifest.ResolveDirectory(_dataDirectory, artifact.Id);
            var script = new ModelLabScriptRunner.ScriptCommand
            {
                Interpreter = interpreter,
                ScriptPath = scriptPath
            };
            // Ascending, always: the script counts a token step when the first listed layer's hook fires.
            var traced = request.Layers?.Order().ToArray() ?? ModelLabTraceSpec.LayersFor(preflight.CheckpointBlockCount ?? 0);
            // Checked at the start: a trace that records attention has both. A steering job records none.
            var attentionLayers = layer is null && request.RecordAttention ? ModelLabTraceSpec.AttentionLayersFor(preflight.CheckpointLayerTypes!) : null;
            // Every block, when there are few enough to cap; a deeper checkpoint traces as it did before the lens. One block has no earlier block to read.
            var lensBlocks = preflight.CheckpointBlockCount is { } blocks and >= 2 and <= ModelLabTraceSpec.MaxTraceLayers ? blocks : (int?)null;
            var tokens = request.MaxNewTokens ?? ModelLabTraceSpec.MaxNewTokens;
            Func<Task<ModelLabExperimentCompletion>> execute = layer is { } steeredLayer
                ? () => ExecuteSteeringAsync(active, request, checkpoint, steeredLayer, script)
                : () => ExecuteTraceAsync(active, new ModelLabTraceJobV1
                {
                    ModelKey = modelKey,
                    CheckpointPath = checkpoint,
                    Prompt = request.Prompt,
                    TracePath = Path.Combine(WorkDirectory(id), TraceFileName),
                    ResultPath = Path.Combine(WorkDirectory(id), ResultFileName),
                    Layers = traced,
                    MaxNewTokens = tokens,
                    TraceByteCap = ModelLabTraceSpec.TraceByteCapFor(traced.Count, tokens),
                    AttentionPath = attentionLayers is null ? null : Path.Combine(WorkDirectory(id), AttentionFileName),
                    AttentionLayers = attentionLayers,
                    AttentionTopKeys = attentionLayers is null ? null : ModelLabTraceSpec.AttentionTopKeys,
                    AttentionByteCap = attentionLayers is null ? null : ModelLabTraceSpec.AttentionByteCapFor(attentionLayers.Count, preflight.CheckpointAttentionHeads!.Value, tokens),
                    LensTopTokens = lensBlocks is null ? null : ModelLabTraceSpec.LensTopTokens,
                    LensByteCap = lensBlocks is { } lensBlockCount ? ModelLabTraceSpec.LensByteCapFor(lensBlockCount, tokens) : null
                }, script);

            lock (_jobLock)
            {
                // Detached: the request's token dies with the HTTP response. The job's own token is what a cancel and a shutdown use.
                job = Task.Run(() => RunAsync(active, execute), CancellationToken.None);
                InFlight = job;
            }

            // After the row exists, so an operations feed's list re-read finds it; the publisher never throws.
            await _jobCreated.PublishCreatedAsync(CreatedJobKind.ModelLabExperiment, id, CancellationToken.None);
            return new ModelLabStart
            {
                ExperimentId = id
            };
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            return Refuse(ModelLabStartRefusal.Busy, StoppingMessage);
        }
        finally
        {
            try
            {
                if (job is null)
                {
                    lock (_jobLock)
                    {
                        _active = null;
                    }

                    active.Cancellation.Dispose();
                    if (admitted)
                    {
                        // The session owns the holds: its eviction gives them back, on its own task.
                        _ = session.ExitActive(keepWarm: false, TimeSpan.Zero);
                    }
                    else
                    {
                        reservation?.Dispose();
                        if (lease is not null)
                        {
                            await lease.DisposeAsync();
                        }

                        admission?.Dispose();
                        session.Abandon();
                    }
                }
            }
            finally
            {
                active.Admission.TrySetResult(job);
            }
        }
    }

    private static async Task<ModelLabStart?> RereadRefusalOrNullAsync(IServiceProvider services, Guid artifactId, CancellationToken ct)
    {
        try
        {
            var artifact = await services.GetRequiredService<IBaseArtifactService>().GetAsync(artifactId, ct);
            return artifact.Status == nameof(TrainingBaseArtifactStatus.Ready) ? null : Refuse(ModelLabStartRefusal.CheckpointNotReady, "The checkpoint has not finished downloading.");
        }
        catch (TrainingNotFoundException)
        {
            return Refuse(ModelLabStartRefusal.CheckpointNotFound, CheckpointNotFoundMessage);
        }
    }

    private async Task CreateRowAsync(IServiceProvider services,
        Guid id,
        ModelLabExperimentRequest request,
        BaseArtifactView artifact,
        ILicenseGateService license,
        CancellationToken ct)
    {
        var view = await license.GetAsync(artifact.Id, ct) ?? throw new TrainingNotFoundException("The base artifact was not found.");
        var confirmation = license.BuildConfirmation(new TrainingLicenseGateView
        {
            BaseArtifactId = view.BaseArtifactId,
            RepoId = view.RepoId,
            License = view.License,
            IsGated = view.IsGated,
            MetadataPresent = view.MetadataPresent,
            ConfirmationText = LicenseGateService.BuildModelLabText(view.RepoId, view.License, view.IsGated, view.MetadataPresent)
        });
        var store = services.GetRequiredService<IModelLabExperimentStore>();
        await store.CreateRunningAsync(new ModelLabExperimentCreate
        {
            Id = id,
            Kind = request.Steering is null ? ModelLabExperimentKind.Trace : ModelLabExperimentKind.Steering,
            RepoId = artifact.RepoId,
            Revision = artifact.Revision,
            BaseArtifactId = artifact.Id,
            InstalledModelName = request.InstalledModelName,
            Prompt = request.Prompt,
            LicenseConfirmationJson = JsonSerializer.SerializeToUtf8Bytes(confirmation, TrainingJson.Options),
            AppVersion = AppVersion
        }, ct);

        await TrimRetainedAsync(store, id);
    }

    /// <summary>The row is committed, and only the job clears it, so neither the request's end nor a failure here may stop the job. The next start trims again.</summary>
    private async Task TrimRetainedAsync(IModelLabExperimentStore store, Guid id)
    {
        try
        {
            foreach (var removed in await store.TrimToNewestAsync(MaxRetainedExperiments, CancellationToken.None))
            {
                _blobs.DeleteScope(removed);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Trimming old Model Lab experiments failed while starting {ExperimentId}.", id);
        }
    }

    /// <summary>The detached job. It ends once its session is idle or evicted, so awaiting it awaits the holds' return.</summary>
    private async Task RunAsync(ActiveJob active, Func<Task<ModelLabExperimentCompletion>> execute)
    {
        var id = active.ExperimentId;
        ModelLabExperimentCompletion? recorded = null;
        ModelLabExperimentCompletion? finished = null;
        try
        {
            ModelLabExperimentCompletion completion;
            try
            {
                completion = await execute();
            }
            catch (Exception) when (active.SaveFailed)
            {
                completion = ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "A result could not be saved to the node's database, so the experiment stopped.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Model Lab experiment {ExperimentId} failed before it could report its own outcome.", id);
                completion = new ModelLabExperimentCompletion
                {
                    Status = ModelLabExperimentStatus.Failed,
                    ErrorMessage = "The experiment failed to run."
                };
            }

            finished = completion;
            await using var scope = _scopeFactory.CreateAsyncScope();
            // A save that failed earlier left the row behind what the job has, and this may still get through.
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>()
                       .CompleteAsync(id, completion with
                       {
                           ResultJson = completion.ResultJson ?? active.LatestResultJson,
                           TraceBlobId = completion.TraceBlobId ?? active.LatestBlob?.BlobId,
                           TraceSha256 = completion.TraceSha256 ?? active.LatestBlob?.Sha256,
                           TraceByteCount = completion.TraceByteCount ?? active.LatestBlob?.ByteCount
                       }, CancellationToken.None);
            recorded = completion;
        }
        catch (Exception exception)
        {
            // The row stays Running until the next startup fails it; the GPU must still come back now.
            _logger.LogError(exception, "Recording the end of Model Lab experiment {ExperimentId} failed.", id);
            // A trace's blobs are named only by the completion that was not saved, so nothing would delete them later. A steering vector is already on the row.
            if (finished?.TraceBlobId is not null)
            {
                _blobs.DeleteScope(id);
            }
        }
        finally
        {
            var settled = Task.CompletedTask;
            try
            {
                BestEffortFile.TryDeleteDirectory(WorkDirectory(id));
                lock (_jobLock)
                {
                    if (ReferenceEquals(_active, active))
                    {
                        _active = null;
                    }
                }

                active.Cancellation.Dispose();
                if (active.ArmsHolds is { } arms)
                {
                    await arms.Lease.DisposeAsync();
                    arms.Gate.Dispose();
                }
                else
                {
                    settled = await LeaveSessionAsync(active);
                }
            }
            finally
            {
                // Last, because a page stops following the job on this event: a list read after it must not name the job, and a new start must be admitted.
                // The session has left Active by now, idle or evicting, so the event never waits out a kill. An unrecorded outcome still ends here.
                _ = _events.Append(id, TrainingRunEventKind.State, new TrainingRunPayload
                {
                    State = (recorded?.Status ?? ModelLabExperimentStatus.Failed).ToString(),
                    Message = recorded is null ? "The experiment's outcome could not be saved." : recorded.ErrorMessage
                });
                _events.EvictPlaintext(id);
            }

            await settled;
        }
    }

    /// <summary>Hands the session on: idle for the node's warm time when the worker came through clean, otherwise evicting.</summary>
    /// <returns>The eviction this started, or a completed task.</returns>
    private async Task<Task> LeaveSessionAsync(ActiveJob active)
    {
        var idleFor = TimeSpan.Zero;
        if (active.WorkerReusable)
        {
            try
            {
                // Read at every idle transition, so a changed setting applies from the next job on and 0 is today's one-shot.
                idleFor = TimeSpan.FromSeconds(await _settings.GetModelLabWarmIdleSecondsAsync(CancellationToken.None));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Reading the Model Lab warm time failed; the worker of experiment {ExperimentId} is evicted.", active.ExperimentId);
            }
        }

        return active.Session.ExitActive(active.WorkerReusable, idleFor);
    }

    private async Task<ModelLabExperimentCompletion> ExecuteTraceAsync(ActiveJob active, ModelLabTraceJobV1 job, ModelLabScriptRunner.ScriptCommand script)
    {
        if (active.Cancellation.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Cancelled();
        }

        var run = await RunServeJobAsync(active, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), script);
        return run.FailureOrNull(active.Cancellation.IsCancellationRequested) ?? await FinishTraceAsync(active.ExperimentId, job);
    }

    private async Task<ModelLabExperimentCompletion> ExecuteSteeringAsync(ActiveJob active,
        ModelLabExperimentRequest request,
        string checkpoint,
        int layer,
        ModelLabScriptRunner.ScriptCommand script)
    {
        if (active.Cancellation.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Cancelled();
        }

        var id = active.ExperimentId;
        var steering = request.Steering!;
        var workDirectory = WorkDirectory(id);
        var job = new ModelLabSteeringJobV1
        {
            ModelKey = active.Session.ModelKey,
            CheckpointPath = checkpoint,
            PositivePrompts = steering.PositivePrompts,
            NegativePrompts = steering.NegativePrompts,
            Layer = layer,
            VectorPath = Path.Combine(workDirectory, VectorFileName),
            RandomVectorPath = Path.Combine(workDirectory, RandomVectorFileName),
            ResultPath = Path.Combine(workDirectory, ResultFileName)
        };

        // Held from here, so the GGUF cannot go while the derive runs and every arm then loads the same file. It hashes the GGUF and can wait behind a
        // download of the same file, with no script yet for a watchdog to read, so the job's own bound is the only one on it.
        await using var scope = _scopeFactory.CreateAsyncScope();
        using var deadline = new CancellationTokenSource(_options.MaxDuration, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(active.Cancellation.Token, deadline.Token);
        ITrainingEvaluationInstalledModelLease model;
        try
        {
            model = await scope.ServiceProvider.GetRequiredService<ITrainingEvaluationInstalledModelLeaseProvider>().AcquireAsync(request.InstalledModelName, linked.Token);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Cancelled();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' could not be verified within the experiment's maximum duration.");
        }
        catch (KeyNotFoundException)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' was not found among the installed models.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InstalledGgufSnapshotException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Holding the GGUF for Model Lab experiment {ExperimentId} failed.", id);
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' could not be read as an installed model ({exception.Message}).");
        }

        await using (model)
        {
            return await DeriveAndRunArmsAsync(active, request, job, script, model.ModelFilePath);
        }
    }

    private async Task<ModelLabExperimentCompletion> DeriveAndRunArmsAsync(ActiveJob active,
        ModelLabExperimentRequest request,
        ModelLabSteeringJobV1 job,
        ModelLabScriptRunner.ScriptCommand script,
        string modelFilePath)
    {
        var id = active.ExperimentId;
        var steering = request.Steering!;
        var run = await RunServeJobAsync(active, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), script);
        if (run.FailureOrNull(active.Cancellation.IsCancellationRequested) is { } failed)
        {
            return failed;
        }

        // The worker goes before the arms, so its memory is back for their own load tickets; only the gate and the lease pass to the job.
        if (await active.Session.DetachForArmsAsync() is not { } holds)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed,
                "The derive's worker was not seen to exit, so it may still hold GPU memory. Model Lab refuses new experiments until it is confirmed gone.");
        }

        active.ArmsHolds = holds;

        var result = await ReadDerivedResultOrNullAsync(job, steering);
        if (result?.Steering is not { } derived)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "The script finished without writing a usable vector.");
        }

        var vectorBytes = await File.ReadAllBytesAsync(job.VectorPath, CancellationToken.None);
        var blobId = Guid.NewGuid();
        var written = await _blobs.WriteAsync(id, blobId, vectorBytes, CancellationToken.None);
        await SetProgressAsync(active, result, new ModelLabBlobReference
        {
            BlobId = blobId,
            Sha256 = written.ContentHash,
            ByteCount = written.ByteCount
        });

        return await RunArmsAsync(active, result, derived, new ModelLabArmPlan
        {
            ModelFilePath = modelFilePath,
            VectorPath = job.VectorPath,
            RandomVectorPath = job.RandomVectorPath,
            Scale = steering.Scale,
            Prompts = [request.Prompt, .. steering.AdditionalTestPrompts]
        }, holds.Lease);
    }

    private static async Task<ModelLabTraceResult?> ReadDerivedResultOrNullAsync(ModelLabSteeringJobV1 job, ModelLabSteeringRequest steering)
    {
        var result = new FileInfo(job.ResultPath);
        var vector = new FileInfo(job.VectorPath);
        var random = new FileInfo(job.RandomVectorPath);
        if (!result.Exists || !vector.Exists || !random.Exists
            || result.Length > ModelLabTraceSpec.MaxResultBytes || vector.Length > MaxVectorBytes || random.Length > MaxVectorBytes)
        {
            return null;
        }

        var read = JsonSerializer.Deserialize<ModelLabTraceResult>(await File.ReadAllBytesAsync(job.ResultPath, CancellationToken.None), JsonSerializerOptions.Web);
        var vectorFile = await FileSha256.ComputeWithLengthAsync(job.VectorPath, CancellationToken.None);
        var randomFile = await FileSha256.ComputeWithLengthAsync(job.RandomVectorPath, CancellationToken.None);
        return read?.Steering is null
            ? null
            : read with
            {
                Steering = read.Steering with
                {
                    Seed = ModelLabTraceSpec.Seed,
                    Scale = steering.Scale,
                    VectorSha256 = vectorFile.Sha256,
                    RandomVectorSha256 = randomFile.Sha256,
                    VectorBytes = vectorFile.SizeBytes,
                    PositivePrompts = steering.PositivePrompts,
                    NegativePrompts = steering.NegativePrompts
                }
            };
    }

    /// <summary>The arms' own bound: the script's watchdog has gone, and each request has its own timeout. Every arm finished by now is already on the row.</summary>
    private async Task<ModelLabExperimentCompletion> RunArmsAsync(ActiveJob active, ModelLabTraceResult result, ModelLabSteeringResult derived, ModelLabArmPlan plan,
        ILlamaServerRuntimeMutationLease lease)
    {
        using var deadline = new CancellationTokenSource(_options.MaxDuration, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(active.Cancellation.Token, deadline.Token);
        try
        {
            var lastExitSeen = await _arms.RunAsync(active.ExperimentId, plan, lease, async arm =>
            {
                derived = derived with
                {
                    Arms = [.. derived.Arms, arm]
                };
                await SetProgressAsync(active, result with
                {
                    Steering = derived
                }, blob: null);
            }, linked.Token);
            return lastExitSeen
                ? new ModelLabExperimentCompletion
                {
                    Status = ModelLabExperimentStatus.Succeeded
                }
                : ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed,
                    "The last arm's server was not seen to exit, so it may still be running and holding GPU memory. Its results are kept. Check for a llama-server process and stop it.");
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Cancelled();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "The arms exceeded their maximum duration, and the unfinished one was stopped.");
        }
        catch (OperationCanceledException)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "An arm's server did not answer in time, and it was stopped.");
        }
        catch (LlamaRuntimeException exception)
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, exception.Message);
        }
        catch (Exception exception) when (!active.SaveFailed)
        {
            _logger.LogError(exception, "An arm of Model Lab experiment {ExperimentId} failed.", active.ExperimentId);
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, $"An arm failed ({exception.Message}). The finished arms are kept.");
        }
    }

    private async Task SetProgressAsync(ActiveJob active, ModelLabTraceResult result, ModelLabBlobReference? blob)
    {
        ReadOnlyMemory<byte> json = JsonSerializer.SerializeToUtf8Bytes(result, JsonSerializerOptions.Web);
        active.LatestResultJson = json;
        active.LatestBlob = blob ?? active.LatestBlob;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetProgressAsync(active.ExperimentId, json, blob, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Saving the progress of Model Lab experiment {ExperimentId} failed.", active.ExperimentId);
            active.SaveFailed = true;
            throw;
        }
    }

    /// <summary>Runs the job in the session's worker, spawning it first on a cold start. The row holds the receipt only while the job runs.</summary>
    private async Task<ModelLabScriptRunner.ScriptRun> RunServeJobAsync(ActiveJob active, byte[] jobJson, ModelLabScriptRunner.ScriptCommand script)
    {
        var id = active.ExperimentId;
        var workDirectory = WorkDirectory(id);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(ModelLabScriptRunner.WorkRoot(_dataDirectory));
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(workDirectory);
        var jobPath = Path.Combine(workDirectory, JobFileName);
        // Owner-only by itself: the directory's own mode is best-effort, and this file holds the plaintext prompt.
        await SecureFilePermissions.WriteAllBytesAtomicAsync(jobPath, jobJson, CancellationToken.None);

        var worker = active.Session.Worker;
        if (worker is null)
        {
#pragma warning disable CA2000 // The session owns the worker from the attach below and disposes it when it evicts.
            worker = ModelLabServeWorker.Spawn(_spawner, script, ModelLabScriptRunner.WorkRoot(_dataDirectory), _options, _timeProvider);
#pragma warning restore CA2000
            var receiptPath = ModelLabWarmReceipts.PathFor(_dataDirectory, worker.RunToken);
            active.Session.AttachWorker(worker, receiptPath);
            var receipt = ModelLabWarmReceipts.Serialize(worker.Receipt);
            try
            {
                // Durable before the first line is read: a host that dies from here on must still find this process.
                await ModelLabWarmReceipts.WriteAsync(receiptPath, receipt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // No warmth without a durable identity: the session evicts a worker it could not record.
                _logger.LogError(exception, "The Model Lab worker's receipt could not be written for experiment {ExperimentId}.", id);
                worker.Kill();
                return new ModelLabScriptRunner.ScriptRun
                {
                    State = new ModelLabScriptRunner.StreamState(),
                    FailureReason = "The Model Lab worker's identity could not be recorded on disk, so it was stopped.",
                    ExitCode = -1
                };
            }

            await SetReceiptAsync(id, receipt);
            worker.StartPump();
            if (await worker.WaitForHandshakeAsync(active.Cancellation.Token) is { } failed)
            {
                await SetReceiptAsync(id, receipt: null);
                return failed;
            }
        }
        else
        {
            await SetReceiptAsync(id, ModelLabWarmReceipts.Serialize(worker.Receipt));
        }

        var outcome = await worker.RunJobAsync(id, jobPath, parsed => ModelLabScriptRunner.PublishFrame(_events, id, parsed), active.Cancellation.Token);
        // A reusable outcome came back with this job's ready, so the sample is this job's.
        if (outcome.Reusable && worker.VramBytes is { } vramBytes)
        {
            _logger.LogInformation("The Model Lab worker for {RepoId} is ready after experiment {ExperimentId}, holding {VramBytes} bytes of allocated GPU memory.",
                active.Session.RepoId, id, vramBytes);
        }

        // The receipt file still names the worker; the row lets go so it can be deleted and trimmed.
        await SetReceiptAsync(id, receipt: null);
        active.WorkerReusable = active.KeepsWorker && outcome.Reusable;
        return outcome.Run;
    }

    /// <summary>A receipt file the startup sweep could not prove gone: a start re-reaps it and is refused while it stays.</summary>
    internal void AdoptUnresolvedReceipt(string receiptPath)
    {
        lock (_jobLock)
        {
            _ = _unresolvedReceipts.TryAdd(receiptPath, new UnresolvedReceipt());
        }
    }

    /// <summary>The startup sweep could not list the receipt files, so any of them may name a live worker: starts are refused until a re-scan succeeds.</summary>
    internal void MarkReceiptScanFailed()
    {
        lock (_jobLock)
        {
            _receiptScanFailed = true;
        }
    }

    /// <summary>Retries the reap of the receipts known to be unresolved, never the directory: a concurrent start's fresh receipt names a live worker.</summary>
    private async Task<ModelLabStart?> OrphanRefusalOrNullAsync(CancellationToken ct)
    {
        string[] known;
        lock (_jobLock)
        {
            if (_receiptScanFailed && RescanLocked() is { } refusal)
            {
                return refusal;
            }

            known = [.. _unresolvedReceipts.Keys];
        }

        var kept = known.Length == 0 ? null : await ModelLabWarmReceipts.ReapAsync(known, HeldReceiptOrNull, _inspector, ReleaseOrphanedReservation, _logger, ct);
        return kept switch
        {
            null => null,
            // Its held copy is reaped instead of the file and can never be probed: only a restart drops it.
            { HeldUnprobeable: true } => Refuse(ModelLabStartRefusal.OrphanWorker, string.Create(CultureInfo.InvariantCulture,
                $"An earlier Model Lab worker ({kept.Name}) was not confirmed stopped, and its recorded identity is too incomplete to check. Confirm that process is gone, remove its stale receipt file from {ModelLabWarmReceipts.Root(_dataDirectory)} if one exists, then restart the node to clear the copy it still holds, and start the experiment again.")),
            // Its held copy is re-reaped by every start and released once the group is empty; a restart would lose that identity.
            { ReceiptPath: null } => Refuse(ModelLabStartRefusal.OrphanWorker, string.Create(CultureInfo.InvariantCulture,
                $"An earlier Model Lab worker ({kept.Name}) was not confirmed stopped, so it may still hold GPU memory. Make sure that worker and its child processes have exited, stopping only processes you confirm belong to it, then start the experiment again.")),
            _ => Refuse(ModelLabStartRefusal.OrphanWorker, string.Create(CultureInfo.InvariantCulture,
                $"An earlier Model Lab worker ({kept.Name}) was not confirmed stopped, so it may still hold GPU memory. Check whether the process it names still belongs to a Model Lab worker: after a restart its number may belong to unrelated processes, so stop only processes you confirm belong to that worker. Once the worker and its child processes are gone, delete its receipt file {kept.ReceiptPath}, then start the experiment again."))
        };
    }

    /// <summary>The startup sweep's listing, retried. Under the job lock, so no start can have spawned a worker whose fresh receipt this would adopt.</summary>
    private ModelLabStart? RescanLocked()
    {
        IReadOnlyList<string> listed;
        try
        {
            listed = ModelLabWarmReceipts.List(_dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The Model Lab worker receipts still cannot be listed.");
            return Refuse(ModelLabStartRefusal.OrphanWorker, string.Create(CultureInfo.InvariantCulture,
                $"Model Lab could not list its worker receipts in {ModelLabWarmReceipts.Root(_dataDirectory)}, so an earlier worker may still hold GPU memory. Make that directory readable by the node, then start the experiment again."));
        }

        foreach (var path in listed)
        {
            _ = _unresolvedReceipts.TryAdd(path, new UnresolvedReceipt());
        }

        _receiptScanFailed = false;
        return null;
    }

    private byte[]? HeldReceiptOrNull(string receiptPath)
    {
        lock (_jobLock)
        {
            return _unresolvedReceipts.TryGetValue(receiptPath, out var unresolved) ? unresolved.Receipt : null;
        }
    }

    /// <summary>An unverified worker's receipt is re-reaped by every start, and its reservation stays on the ledger until that reap proves it gone.</summary>
    private void OnOrphaned(string receiptPath, byte[]? receipt, TrainingCapacityReservation? reservation, Guid baseArtifactId)
    {
        lock (_jobLock)
        {
            _unresolvedReceipts[receiptPath] = new UnresolvedReceipt
            {
                Receipt = receipt,
                Reservation = reservation,
                BaseArtifactId = baseArtifactId
            };
        }
    }

    private void ReleaseOrphanedReservation(string receiptPath)
    {
        UnresolvedReceipt? unresolved;
        lock (_jobLock)
        {
            if (!_unresolvedReceipts.Remove(receiptPath, out unresolved))
            {
                return;
            }
        }

        unresolved.Reservation?.Dispose();
    }

    private async Task<ModelLabExperimentCompletion> FinishTraceAsync(Guid id, ModelLabTraceJobV1 job)
    {
        var trace = new FileInfo(job.TracePath);
        var result = new FileInfo(job.ResultPath);
        // The script writes the attention file whenever it was asked to, with only a footer when it kept no row.
        var attention = job.AttentionPath is { } attentionPath ? new FileInfo(attentionPath) : null;
        if (!trace.Exists || !result.Exists || attention is { Exists: false })
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "The Model Lab script finished without writing its results.");
        }

        // The script holds the cap itself. This is the check that does not depend on the script being right.
        if (trace.Length > job.TraceByteCap + (job.LensByteCap ?? 0) || result.Length > ModelLabTraceSpec.MaxResultBytes || (attention is not null && attention.Length > job.AttentionByteCap))
        {
            return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, "The Model Lab script wrote more than its byte cap, so nothing was kept.");
        }

        var resultJson = await File.ReadAllBytesAsync(job.ResultPath, CancellationToken.None);
        var blobId = Guid.NewGuid();
        Guid? attentionBlobId = attention is null ? null : Guid.NewGuid();
        ManagedBlobWriteResult written;
        ManagedBlobWriteResult? attentionWritten = null;
        try
        {
            written = await _blobs.WriteAsync(id, blobId, await File.ReadAllBytesAsync(job.TracePath, CancellationToken.None), CancellationToken.None);
            if (attentionBlobId is { } second)
            {
                BeforeAttentionBlobWrite?.Invoke();
                attentionWritten = await _blobs.WriteAsync(id, second, await File.ReadAllBytesAsync(job.AttentionPath!, CancellationToken.None), CancellationToken.None);
            }
        }
        catch
        {
            // No row names these blobs yet, so a half-published run would leave them on disk for good.
            _blobs.DeleteScope(id);
            throw;
        }

        return new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded,
            ResultJson = resultJson,
            TraceBlobId = blobId,
            TraceSha256 = written.ContentHash,
            TraceByteCount = written.ByteCount,
            AttentionBlobId = attentionBlobId,
            AttentionSha256 = attentionWritten?.ContentHash,
            AttentionByteCount = attentionWritten?.ByteCount
        };
    }

    private async Task SetReceiptAsync(Guid id, ReadOnlyMemory<byte>? receipt)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, receipt, CancellationToken.None);
    }

    private string WorkDirectory(Guid id) =>
        Path.Combine(ModelLabScriptRunner.WorkRoot(_dataDirectory), id.ToString("N"));

#pragma warning disable CA2000 // The detached job disposes its token source; the start's finally does on any failure before that.
    private static ActiveJob NewJob(Guid id, Guid baseArtifactId, ModelLabGpuSession session, int? layer) =>
        new()
        {
            ExperimentId = id,
            BaseArtifactId = baseArtifactId,
            Cancellation = new CancellationTokenSource(),
            Session = session,
            KeepsWorker = layer is null
        };
#pragma warning restore CA2000

    private static ModelLabStart Refuse(ModelLabStartRefusal refusal, string message) =>
        new()
        {
            Refusal = refusal,
            RefusalMessage = message
        };

    /// <summary>What a start re-reaps by: the receipt still held in memory, which may never have reached its file, and the reservation kept for it.</summary>
    private sealed class UnresolvedReceipt
    {
        public byte[]? Receipt { get; init; }

        public TrainingCapacityReservation? Reservation { get; init; }

        /// <summary>The checkpoint the worker held; null for a receipt the startup sweep kept.</summary>
        public Guid? BaseArtifactId { get; init; }
    }

    private sealed class DeleteClaim : IDisposable
    {
        private readonly ModelLabExperimentService _owner;
        private readonly Guid _baseArtifactId;
        private int _released;

        public DeleteClaim(ModelLabExperimentService owner, Guid baseArtifactId)
        {
            _owner = owner;
            _baseArtifactId = baseArtifactId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.ReleaseDeleteClaim(_baseArtifactId);
            }
        }
    }

    private sealed class ActiveJob
    {
        public required Guid ExperimentId { get; init; }

        public required Guid BaseArtifactId { get; init; }

        public required CancellationTokenSource Cancellation { get; init; }

        /// <summary>The session the job borrows, warm or just admitted.</summary>
        public required ModelLabGpuSession Session { get; init; }

        /// <summary>A trace may leave its worker warm; a steering job never does.</summary>
        public required bool KeepsWorker { get; init; }

        /// <summary>Set by the job's own flow once its worker came through clean and may stay warm.</summary>
        public bool WorkerReusable { get; set; }

        /// <summary>A steering job's gate and lease once its worker is gone; the job gives them back after its arms.</summary>
        public ModelLabArmsHolds? ArmsHolds { get; set; }

        /// <summary>Completes when the start has let go: with this job's task, or with null when it ended without one, every hold already given back.</summary>
        public TaskCompletionSource<Task?> Admission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Written and read under the job lock.</summary>
        public bool RowCommitted { get; set; }

        /// <summary>What the job last tried to save. Only the job's own flow touches it, in order.</summary>
        public ReadOnlyMemory<byte>? LatestResultJson { get; set; }

        /// <summary>The blob that result refers to, kept with it for the same reason.</summary>
        public ModelLabBlobReference? LatestBlob { get; set; }

        /// <summary>Set when that save threw, so the failure is reported as the store's and not as the arm's or its server's.</summary>
        public bool SaveFailed { get; set; }
    }
}
