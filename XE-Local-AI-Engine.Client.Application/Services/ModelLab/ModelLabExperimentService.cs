namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Admits, runs and records Model Lab's trace jobs: one at a time, refused when busy, with no queue.</summary>
/// <remarks>
///     Every refusal is decided before anything is written. Admission is the path a training run takes: the exclusive gate hold, the
///     runtime-mutation lease (null while a model is loaded, which refuses with no eject), then the byte reservation other spawns read.
///     While the lease is held a chat that needs a local model waits
///     in the supervisor. Prompts and vectors reach disk only in the owner-only work directory, deleted on every path and at startup. A
///     steering job gives its reservation back after the derive and keeps the gate and the lease across both phases.
/// </remarks>
public sealed class ModelLabExperimentService : IHostedService, IModelLabActiveJob
{
    public const int MaxRetainedExperiments = 20;

    private const string TraceFileName = "trace.jsonl";
    private const string ResultFileName = "result.json";
    private const string VectorFileName = "steer.gguf";
    private const string RandomVectorFileName = "random.gguf";

    /// <summary>A trace outlives no reason to be larger than its cap, and a vector for any model that fits a card is smaller still.</summary>
    private const int MaxBlobBytes = ModelLabTraceSpec.TraceByteCap;

    private readonly ModelLabArmRunner _arms;
    private readonly ManagedEncryptedBlobStore _blobs;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ITrainingRunEventBuffer _events;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly Lock _jobLock = new();
    private readonly ILogger<ModelLabExperimentService> _logger;
    private readonly ModelLabJobOptions _options;
    private readonly ModelLabCheckpointPreflightService _preflight;
    private readonly ITrainingRuntimeService _runtime;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModelLabScriptRunner _scripts;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private ActiveJob? _active;

    public ModelLabExperimentService(IServiceScopeFactory scopeFactory,
        ModelLabCheckpointPreflightService preflight,
        IGpuWorkGate gpuWorkGate,
        ILlamaServerProcessSupervisor supervisor,
        ITrainingRuntimeService runtime,
        ITrainingProcessSpawner spawner,
        ITrainingRunEventBuffer events,
        ModelLabArmRunner arms,
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        IOptions<ModelLabJobOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelLabExperimentService> logger)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(keyHolder);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _arms = arms;
        _blobs = new ManagedEncryptedBlobStore(dataDirectory, keyHolder, "model-lab", "artifacts", "model_lab_artifact_blob", MaxBlobBytes, "Model Lab artifact");
        _dataDirectory = dataDirectory;
        _events = events;
        _gpuWorkGate = gpuWorkGate;
        _logger = logger;
        _options = options.Value;
        _preflight = preflight;
        _runtime = runtime;
        _scopeFactory = scopeFactory;
        _scripts = new ModelLabScriptRunner(spawner, events, _options, timeProvider);
        _supervisor = supervisor;
        _timeProvider = timeProvider;
    }

    /// <summary>The job is Linux only. A test swaps this to see the other platforms' answer.</summary>
    internal Func<bool> PlatformProbe { get; set; } = OperatingSystem.IsLinux;

    public bool IsPlatformSupported => PlatformProbe();

    public static string UnsupportedPlatformMessage =>
        "Tracing runs on Linux only for now, because it needs the Python training runtime, which is available on Linux only. The other Model Lab tools work here.";

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

    public Guid? ActiveBaseArtifactId
    {
        get
        {
            lock (_jobLock)
            {
                return _active?.BaseArtifactId;
            }
        }
    }

    public static string WorkRoot(INodeDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(dataDirectory.Root, "model-lab", "work");
    }

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
            return Refuse(ModelLabStartRefusal.CheckpointNotFound, "The checkpoint was not found. Download it first.");
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
        if (_runtime.ResolveInterpreterPath() is not { } interpreter
            || _runtime.GetStatus().Phase != TrainingRuntimePhase.Ready
            || !File.Exists(scriptPath))
        {
            return Refuse(ModelLabStartRefusal.RuntimeUnavailable, "The Python training runtime is not installed. Install it on the Training page, then start the trace again.");
        }

        if (request.Steering is null)
        {
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

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Host shutdown: the job is cancelled like an operator's cancel and awaited, so no script outlives a clean stop.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveJob? active;
        lock (_jobLock)
        {
            active = _active;
        }

        if (active is null)
        {
            return;
        }

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

    private async Task<ModelLabStart> AdmitAndStartAsync(ModelLabExperimentRequest request,
        BaseArtifactView artifact,
        ModelLabCheckpointPreflight preflight,
        int? layer,
        string interpreter,
        string scriptPath,
        IServiceProvider services,
        CancellationToken ct)
    {
#pragma warning disable CA2000 // Disposed on every refusal below, otherwise owned by the detached job.
        if (!_gpuWorkGate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out var admission))
#pragma warning restore CA2000
        {
            return Refuse(ModelLabStartRefusal.Busy, "The GPU is busy with other work. Wait for it to finish, or cancel it, and start the experiment again.");
        }

        ILlamaServerRuntimeMutationLease? lease = null;
        TrainingCapacityReservation? reservation = null;
        var id = Guid.NewGuid();
#pragma warning disable CA2000 // The detached job disposes its token source; the finally below does on any failure before that.
        var active = new ActiveJob
        {
            ExperimentId = id,
            BaseArtifactId = artifact.Id,
            Cancellation = new CancellationTokenSource()
        };
#pragma warning restore CA2000
        // Held before the lease wait and before the row, so a delete in the wait is refused and nothing can read a row of a real job as one with no job.
        lock (_jobLock)
        {
            _active = active;
        }

        Task? job = null;
        try
        {
            // The delete guard could not see this job until the line above, so the first read may be stale.
            // Accepted ceiling: a delete already past its guard can still land after this read; closing that needs a lock shared with the delete.
            if (await RereadRefusalOrNullAsync(services, artifact.Id, ct) is { } gone)
            {
                return gone;
            }

            // For the two waits only, which a host stop must not sit out. From the row's commit on, a cancel is the job's to see at its first step.
            using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Cancellation.Token);
            lease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(waits.Token);
            if (lease is null)
            {
                return Refuse(ModelLabStartRefusal.ModelLoaded, "A model is loaded. Eject it, then start the experiment again.");
            }

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
            var heldLease = lease;
            var heldReservation = reservation;
#pragma warning disable CA2025 // The job owns all three holds from here; job below keeps this method from disposing them.
            Func<Task<ModelLabExperimentCompletion>> execute = layer is { } steeredLayer
                ? () => ExecuteSteeringAsync(active, request, checkpoint, steeredLayer, script, heldLease, heldReservation)
                : () => ExecuteTraceAsync(active, new ModelLabTraceJobV1
                {
                    CheckpointPath = checkpoint,
                    Prompt = request.Prompt,
                    TracePath = Path.Combine(WorkDirectory(id), TraceFileName),
                    ResultPath = Path.Combine(WorkDirectory(id), ResultFileName),
                    Layers = ModelLabTraceSpec.LayersFor(preflight.CheckpointBlockCount ?? 0)
                }, script);

            lock (_jobLock)
            {
                var heldAdmission = admission;
                // Detached: the request's token dies with the HTTP response. The job's own token is what a cancel and a shutdown use.
                job = Task.Run(() => RunAsync(active, execute, heldAdmission, heldLease, heldReservation), CancellationToken.None);
                InFlight = job;
#pragma warning restore CA2025
            }

            return new ModelLabStart
            {
                ExperimentId = id
            };
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            return Refuse(ModelLabStartRefusal.Busy, "The node is stopping. Start the experiment again once it is back.");
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
                    reservation?.Dispose();
                    if (lease is not null)
                    {
                        await lease.DisposeAsync();
                    }

                    admission.Dispose();
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
            return Refuse(ModelLabStartRefusal.CheckpointNotFound, "The checkpoint was not found. Download it first.");
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
            LicenseConfirmationJson = JsonSerializer.SerializeToUtf8Bytes(confirmation, TrainingJson.Options)
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

    private async Task RunAsync(ActiveJob active,
        Func<Task<ModelLabExperimentCompletion>> execute,
        IDisposable admission,
        ILlamaServerRuntimeMutationLease lease,
        TrainingCapacityReservation reservation)
    {
        var id = active.ExperimentId;
        ModelLabExperimentCompletion? recorded = null;
        try
        {
            ModelLabExperimentCompletion completion;
            try
            {
                completion = await execute();
            }
            catch (Exception) when (active.SaveFailed)
            {
                completion = Finished(ModelLabExperimentStatus.Failed, "A result could not be saved to the node's database, so the experiment stopped.");
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
        }
        finally
        {
            try
            {
                BestEffortFile.TryDeleteDirectory(WorkDirectory(id));
                lock (_jobLock)
                {
                    _active = null;
                }

                active.Cancellation.Dispose();
                reservation.Dispose();
                await lease.DisposeAsync();
                admission.Dispose();
            }
            finally
            {
                // Last, because a page stops following the job on this event: a list read after it must not name the job, and a new start must be admitted.
                // An outcome that could not be recorded still ends here, or the page would wait on a job that has let go of everything.
                _ = _events.Append(id, TrainingRunEventKind.State, new TrainingRunPayload
                {
                    State = (recorded?.Status ?? ModelLabExperimentStatus.Failed).ToString(),
                    Message = recorded is null ? "The experiment's outcome could not be saved." : recorded.ErrorMessage
                });
                _events.EvictPlaintext(id);
            }
        }
    }

    private async Task<ModelLabExperimentCompletion> ExecuteTraceAsync(ActiveJob active, ModelLabTraceJobV1 job, ModelLabScriptRunner.ScriptCommand script)
    {
        if (active.Cancellation.IsCancellationRequested)
        {
            return Cancelled();
        }

        var run = await RunScriptAsync(active, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), script);
        return run.FailureOrNull(active.Cancellation.IsCancellationRequested) ?? await FinishTraceAsync(active.ExperimentId, job);
    }

    private async Task<ModelLabExperimentCompletion> ExecuteSteeringAsync(ActiveJob active,
        ModelLabExperimentRequest request,
        string checkpoint,
        int layer,
        ModelLabScriptRunner.ScriptCommand script,
        ILlamaServerRuntimeMutationLease lease,
        TrainingCapacityReservation reservation)
    {
        if (active.Cancellation.IsCancellationRequested)
        {
            return Cancelled();
        }

        var id = active.ExperimentId;
        var steering = request.Steering!;
        var workDirectory = WorkDirectory(id);
        var job = new ModelLabSteeringJobV1
        {
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
            return Cancelled();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' could not be verified within the experiment's maximum duration.");
        }
        catch (KeyNotFoundException)
        {
            return Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' was not found among the installed models.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InstalledGgufSnapshotException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Holding the GGUF for Model Lab experiment {ExperimentId} failed.", id);
            return Finished(ModelLabExperimentStatus.Failed, $"'{request.InstalledModelName}' could not be read as an installed model ({exception.Message}).");
        }

        await using (model)
        {
            return await DeriveAndRunArmsAsync(active, request, job, script, model.ModelFilePath, lease, reservation);
        }
    }

    private async Task<ModelLabExperimentCompletion> DeriveAndRunArmsAsync(ActiveJob active,
        ModelLabExperimentRequest request,
        ModelLabSteeringJobV1 job,
        ModelLabScriptRunner.ScriptCommand script,
        string modelFilePath,
        ILlamaServerRuntimeMutationLease lease,
        TrainingCapacityReservation reservation)
    {
        var id = active.ExperimentId;
        var steering = request.Steering!;
        var run = await RunScriptAsync(active, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), script);

        // The script is gone: its receipt must not name a recycled process, and its memory is back for the arms' own load tickets.
        await SetReceiptAsync(id, receipt: null);
        reservation.Dispose();
        if (run.FailureOrNull(active.Cancellation.IsCancellationRequested) is { } failed)
        {
            return failed;
        }

        var result = await ReadDerivedResultOrNullAsync(job, steering);
        if (result?.Steering is not { } derived)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The script finished without writing a usable vector.");
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
        }, lease);
    }

    private static async Task<ModelLabTraceResult?> ReadDerivedResultOrNullAsync(ModelLabSteeringJobV1 job, ModelLabSteeringRequest steering)
    {
        var result = new FileInfo(job.ResultPath);
        var vector = new FileInfo(job.VectorPath);
        var random = new FileInfo(job.RandomVectorPath);
        if (!result.Exists || !vector.Exists || !random.Exists
            || result.Length > ModelLabTraceSpec.MaxResultBytes || vector.Length > MaxBlobBytes || random.Length > MaxBlobBytes)
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
                : Finished(ModelLabExperimentStatus.Failed,
                    "The last arm's server was not seen to exit, so it may still be running and holding GPU memory. Its results are kept. Check for a llama-server process and stop it.");
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The arms exceeded their maximum duration, and the unfinished one was stopped.");
        }
        catch (OperationCanceledException)
        {
            return Finished(ModelLabExperimentStatus.Failed, "An arm's server did not answer in time, and it was stopped.");
        }
        catch (LlamaRuntimeException exception)
        {
            return Finished(ModelLabExperimentStatus.Failed, exception.Message);
        }
        catch (Exception exception) when (!active.SaveFailed)
        {
            _logger.LogError(exception, "An arm of Model Lab experiment {ExperimentId} failed.", active.ExperimentId);
            return Finished(ModelLabExperimentStatus.Failed, $"An arm failed ({exception.Message}). The finished arms are kept.");
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

    private Task<ModelLabScriptRunner.ScriptRun> RunScriptAsync(ActiveJob active, byte[] jobJson, ModelLabScriptRunner.ScriptCommand script) =>
        _scripts.RunAsync(active.ExperimentId,
            WorkDirectory(active.ExperimentId),
            jobJson,
            script,
            receipt => SetReceiptAsync(active.ExperimentId, receipt),
            active.Cancellation.Token);

    private async Task<ModelLabExperimentCompletion> FinishTraceAsync(Guid id, ModelLabTraceJobV1 job)
    {
        var trace = new FileInfo(job.TracePath);
        var result = new FileInfo(job.ResultPath);
        if (!trace.Exists || !result.Exists)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The Model Lab script finished without writing its results.");
        }

        // The script holds the cap itself. This is the check that does not depend on the script being right.
        if (trace.Length > job.TraceByteCap || result.Length > ModelLabTraceSpec.MaxResultBytes)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The Model Lab script wrote more than its byte cap, so nothing was kept.");
        }

        var blobId = Guid.NewGuid();
        var written = await _blobs.WriteAsync(id, blobId, await File.ReadAllBytesAsync(job.TracePath, CancellationToken.None), CancellationToken.None);
        return new ModelLabExperimentCompletion
        {
            Status = ModelLabExperimentStatus.Succeeded,
            ResultJson = await File.ReadAllBytesAsync(job.ResultPath, CancellationToken.None),
            TraceBlobId = blobId,
            TraceSha256 = written.ContentHash,
            TraceByteCount = written.ByteCount
        };
    }

    private async Task SetReceiptAsync(Guid id, ReadOnlyMemory<byte>? receipt)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, receipt, CancellationToken.None);
    }

    private string WorkDirectory(Guid id) =>
        Path.Combine(WorkRoot(_dataDirectory), id.ToString("N"));

    internal static ModelLabExperimentCompletion Cancelled() =>
        Finished(ModelLabExperimentStatus.Cancelled, "The experiment was cancelled.");

    internal static ModelLabExperimentCompletion Finished(ModelLabExperimentStatus status, string message) =>
        new()
        {
            Status = status,
            ErrorMessage = message
        };

    private static ModelLabStart Refuse(ModelLabStartRefusal refusal, string message) =>
        new()
        {
            Refusal = refusal,
            RefusalMessage = message
        };

    private sealed class ActiveJob
    {
        public required Guid ExperimentId { get; init; }

        public required Guid BaseArtifactId { get; init; }

        public required CancellationTokenSource Cancellation { get; init; }

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
