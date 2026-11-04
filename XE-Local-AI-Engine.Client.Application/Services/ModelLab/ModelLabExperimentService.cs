namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Admits, runs and records Model Lab's trace jobs: one at a time, refused when busy, with no queue.</summary>
/// <remarks>
///     Every refusal is decided before anything is written. Admission is the path a training run takes: the exclusive
///     <see cref="IGpuWorkGate" /> hold, the runtime-mutation lease (null while a model is loaded, which is how a loaded model blocks the
///     start with no eject), then the byte reservation other spawns read. While the lease is held a chat that needs a local model waits
///     in the supervisor, and <c>GpuWorkHolderNotice</c> says so. The prompt reaches disk only in the owner-only work directory, deleted on
///     every path here and at startup by <see cref="ModelLabStartupReaper" />.
/// </remarks>
public sealed class ModelLabExperimentService : IHostedService, IModelLabActiveJob
{
    public const int MaxRetainedExperiments = 20;

    private const string TraceFileName = "trace.jsonl";
    private const string ResultFileName = "result.json";
    private const string JobFileName = "job.json";

    /// <summary>A trace outlives no reason to be larger than its cap; S4's vectors are smaller still.</summary>
    private const int MaxBlobBytes = ModelLabTraceSpec.TraceByteCap;

    private static readonly JsonSerializerOptions TraceJson = new();

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
    private readonly ITrainingProcessSpawner _spawner;
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
        INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        IOptions<ModelLabJobOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelLabExperimentService> logger)
    {
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
        _blobs = new ManagedEncryptedBlobStore(dataDirectory, keyHolder, "model-lab", "artifacts", "model_lab_artifact_blob", MaxBlobBytes, "Model Lab artifact");
        _dataDirectory = dataDirectory;
        _events = events;
        _gpuWorkGate = gpuWorkGate;
        _logger = logger;
        _options = options.Value;
        _preflight = preflight;
        _runtime = runtime;
        _scopeFactory = scopeFactory;
        _spawner = spawner;
        _supervisor = supervisor;
        _timeProvider = timeProvider;
    }

    /// <summary>D2: the job is Linux only for now. A test swaps this to see the other platforms' answer.</summary>
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

    public async Task<ModelLabStart> StartTraceAsync(ModelLabTraceRequest request, CancellationToken ct)
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

        return await AdmitAndStartAsync(request, artifact, preflight, blockCount, interpreter, scriptPath, services, ct);
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
        if (record is not { TraceBlobId: { } blobId, TraceSha256: { } hash, TraceByteCount: { } bytes })
        {
            return new ModelLabTrace { Status = ModelLabTraceReadStatus.NotFound };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        return read.Status == ManagedBlobReadStatus.Found
            ? ParseTrace(read.Content)
            : new ModelLabTrace { Status = ModelLabTraceReadStatus.Unavailable };
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Host shutdown: the job is cancelled like an operator's cancel and awaited, so no script outlives a clean stop.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveJob? active;
        Task? inFlight;
        lock (_jobLock)
        {
            active = _active;
            inFlight = InFlight;
        }

        if (active is null || inFlight is null)
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

        await inFlight.WaitAsync(cancellationToken);
    }

    internal static ModelLabTrace ParseTrace(ReadOnlyMemory<byte> content)
    {
        var records = new List<ModelLabTraceRecord>();
        ModelLabTraceFooter? footer = null;
        foreach (var line in Encoding.UTF8.GetString(content.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (line.StartsWith("{\"footer\"", StringComparison.Ordinal))
                {
                    footer = JsonSerializer.Deserialize<ModelLabTraceFooter>(line, TraceJson);
                }
                else if (JsonSerializer.Deserialize<ModelLabTraceRecord>(line, TraceJson) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // The blob's hash was verified, so this is a script bug, not tampering; the trace is shown as unreadable.
                return new ModelLabTrace { Status = ModelLabTraceReadStatus.Unavailable };
            }
        }

        return new ModelLabTrace
        {
            Status = ModelLabTraceReadStatus.Found,
            Records = records,
            Footer = footer
        };
    }

    private async Task<ModelLabStart> AdmitAndStartAsync(ModelLabTraceRequest request,
        BaseArtifactView artifact,
        ModelLabCheckpointPreflight preflight,
        int blockCount,
        string interpreter,
        string scriptPath,
        IServiceProvider services,
        CancellationToken ct)
    {
#pragma warning disable CA2000 // Disposed on every refusal below, otherwise owned by the detached job.
        if (!_gpuWorkGate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out var admission))
#pragma warning restore CA2000
        {
            return Refuse(ModelLabStartRefusal.Busy, "The GPU is busy with other work. Wait for it to finish, or cancel it, and start the trace again.");
        }

        ILlamaServerRuntimeMutationLease? lease = null;
        TrainingCapacityReservation? reservation = null;
        ActiveJob? active = null;
        var handedOff = false;
        try
        {
            lease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(ct);
            if (lease is null)
            {
                return Refuse(ModelLabStartRefusal.ModelLoaded, "A model is loaded. Eject it, then start the trace again.");
            }

            reservation = await services.GetRequiredService<ITrainingCapacityGate>().ReserveAsync(new TrainingFootprintEstimate
            {
                GpuBytes = preflight.RequiredVramBytes ?? 0,
                RamBytes = ModelLabCheckpointPreflightService.WorkingMemoryBytes,
                ParameterCount = 0,
                TrainableParameterCount = 0,
                Experimental = false
            }, ct);
            if (!reservation.Granted)
            {
                return Refuse(ModelLabStartRefusal.InsufficientMemory, reservation.Reason ?? "There is not enough free memory to start the trace.");
            }

            var license = services.GetRequiredService<ILicenseGateService>();
            var id = Guid.NewGuid();
#pragma warning disable CA2000 // The detached job disposes its token source; the finally below does on any failure before that.
            active = new ActiveJob
            {
                ExperimentId = id,
                BaseArtifactId = artifact.Id,
                Cancellation = new CancellationTokenSource()
            };
#pragma warning restore CA2000
            // Held before the row exists, so nothing can read a row of a real job as one with no job.
            lock (_jobLock)
            {
                _active = active;
            }

            await CreateRowAsync(services, id, request, artifact, license, ct);
            lock (_jobLock)
            {
                active.RowCommitted = true;
            }

            var job = new ModelLabTraceJobV1
            {
                CheckpointPath = BaseArtifactManifest.ResolveDirectory(_dataDirectory, artifact.Id),
                Prompt = request.Prompt,
                TracePath = Path.Combine(WorkDirectory(id), TraceFileName),
                ResultPath = Path.Combine(WorkDirectory(id), ResultFileName),
                Layers = ModelLabTraceSpec.LayersFor(blockCount)
            };

            lock (_jobLock)
            {
                var heldActive = active;
                var heldAdmission = admission;
                var heldLease = lease;
                var heldReservation = reservation;
                // Detached: the request's token dies with the HTTP response. The job's own token is what a cancel and a shutdown use.
#pragma warning disable CA2025 // The job owns all three holds from here; handedOff below keeps this method from disposing them.
                InFlight = Task.Run(() => RunAsync(heldActive, job, interpreter, scriptPath, heldAdmission, heldLease, heldReservation), CancellationToken.None);
#pragma warning restore CA2025
            }

            handedOff = true;
            return new ModelLabStart { ExperimentId = id };
        }
        finally
        {
            if (!handedOff)
            {
                if (active is not null)
                {
                    lock (_jobLock)
                    {
                        _active = null;
                    }

                    active.Cancellation.Dispose();
                }

                reservation?.Dispose();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                admission.Dispose();
            }
        }
    }

    private async Task CreateRowAsync(IServiceProvider services,
        Guid id,
        ModelLabTraceRequest request,
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
        ModelLabTraceJobV1 job,
        string interpreter,
        string scriptPath,
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
                completion = await ExecuteAsync(active, job, interpreter, scriptPath);
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
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CompleteAsync(id, completion, CancellationToken.None);
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
                if (recorded is not null)
                {
                    _ = _events.Append(id, TrainingRunEventKind.State, new TrainingRunPayload
                    {
                        State = recorded.Status.ToString(),
                        Message = recorded.ErrorMessage
                    });
                }

                _events.EvictPlaintext(id);
            }
        }
    }

    private async Task<ModelLabExperimentCompletion> ExecuteAsync(ActiveJob active, ModelLabTraceJobV1 job, string interpreter, string scriptPath)
    {
        var id = active.ExperimentId;
        var workDirectory = WorkDirectory(id);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(Path.GetDirectoryName(workDirectory)!);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(workDirectory);
        var jobPath = Path.Combine(workDirectory, JobFileName);
        // Owner-only by itself: the directory's own mode is best-effort, and this file holds the plaintext prompt.
        await SecureFilePermissions.WriteAllBytesAtomicAsync(jobPath, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), CancellationToken.None);

        using var handle = _spawner.Spawn(new TrainingSpawnRequest
        {
            ExecutablePath = interpreter,
            Arguments = [scriptPath, "--config", jobPath],
            WorkingDirectory = workDirectory,
            RunToken = Guid.NewGuid().ToString("N")
        });

        // Durable before the first line is read: a host that dies now must still be able to prove which process is this job's.
        await SetReceiptAsync(id, JsonSerializer.SerializeToUtf8Bytes(TrainingProcessReaper.ToPersisted(handle.Receipt), TrainingJson.Options));

        var state = new StreamState(_timeProvider.GetUtcNow());
        var stop = active.Cancellation.Token.Register(() =>
        {
            state.MarkStopRequested(_timeProvider.GetUtcNow());
            handle.RequestStop();
        });
        using var watchdogStop = new CancellationTokenSource();
        var watchdog = WatchdogAsync(handle.KillGroup, state, watchdogStop.Token);
        try
        {
            await foreach (var line in handle.ReadOutputAsync(CancellationToken.None))
            {
                if (TrainingRunStdioParser.ParseOrNull(line) is { } parsed)
                {
                    state.MarkEvent(_timeProvider.GetUtcNow());
                    Apply(id, state, parsed, handle.KillGroup);
                }
                else
                {
                    state.KeepOutput(line);
                }
            }
        }
        finally
        {
            await watchdogStop.CancelAsync();
            await watchdog;
            await stop.DisposeAsync();
        }

        var exitCode = await WaitForExitOrEscalateAsync(handle, state);
        return await FinishAsync(id, job, state, exitCode, active.Cancellation.IsCancellationRequested);
    }

    private void Apply(Guid id, StreamState state, TrainingStdioEvent parsed, Action killGroup)
    {
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Handshake:
                state.HandshakeSeen = true;
                if (parsed.ContractVersion != ModelLabTraceSpec.ContractVersion)
                {
                    state.FailureReason = "The trace script's protocol version does not match this build.";
                    killGroup();
                }

                break;
            case TrainingStdioEventKind.Phase:
                _ = _events.Append(id, TrainingRunEventKind.Phase, new TrainingRunPayload
                {
                    Phase = parsed.Phase
                });
                break;
            case TrainingStdioEventKind.Progress:
                _ = _events.Append(id, TrainingRunEventKind.Progress, new TrainingRunPayload
                {
                    Step = parsed.Step,
                    TotalSteps = parsed.TotalSteps,
                    VramBytes = parsed.VramBytes
                });
                break;
            case TrainingStdioEventKind.Done:
                state.Done = true;
                state.Cancelled |= parsed.Cancelled;
                break;
            case TrainingStdioEventKind.Error:
                state.ErrorMessage = parsed.Message ?? parsed.Category;
                break;
            case TrainingStdioEventKind.Heartbeat:
            case TrainingStdioEventKind.Artifact:
            default:
                break;
        }
    }

    private async Task WatchdogAsync(Action killGroup, StreamState state, CancellationToken ct)
    {
        // Scaled down with the bounds, so a short one is not rounded up to the poll interval.
        var shortest = _options.ExitGracePeriod < _options.InactivityTimeout ? _options.ExitGracePeriod : _options.InactivityTimeout;
        var interval = shortest < TimeSpan.FromSeconds(4) ? shortest / 4 : TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (state.StopRequestedAt() is { } stopAt)
            {
                // A cooperative stop is what a wedged CUDA call cannot do. The outcome is still the Cancelled the operator asked for.
                if (now - stopAt > _options.ExitGracePeriod)
                {
                    killGroup();
                    return;
                }

                continue;
            }

            if (now - state.LastEventAt() > _options.InactivityTimeout)
            {
                state.FailureReason ??= "The trace script stopped reporting and was terminated.";
            }
            else if (now - state.StartedAt > _options.MaxDuration)
            {
                state.FailureReason ??= "The trace exceeded its maximum duration and was terminated.";
            }

            if (state.FailureReason is not null)
            {
                killGroup();
                return;
            }
        }
    }

    /// <summary>A script that closed its output and then wedged would otherwise hold the GPU forever.</summary>
    private async Task<int> WaitForExitOrEscalateAsync(ITrainingProcessHandle handle, StreamState state)
    {
        using var grace = new CancellationTokenSource(_options.ExitGracePeriod, _timeProvider);
        try
        {
            return await handle.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            handle.KillGroup();
            state.FailureReason ??= "The trace script stopped responding after closing its output and was terminated.";
            return -1;
        }
    }

    private async Task<ModelLabExperimentCompletion> FinishAsync(Guid id, ModelLabTraceJobV1 job, StreamState state, int exitCode, bool cancelRequested)
    {
        if (cancelRequested || state.Cancelled || exitCode == TrainingRunExecutor.CancelledExitCode)
        {
            return Finished(ModelLabExperimentStatus.Cancelled, "The experiment was cancelled.");
        }

        if (state.FailureReason is { } reason)
        {
            return Finished(ModelLabExperimentStatus.Failed, reason);
        }

        if (state.ErrorMessage is { Length: > 0 } error)
        {
            return Finished(ModelLabExperimentStatus.Failed, state.WithLastOutput(error));
        }

        if (!state.HandshakeSeen || !state.Done || exitCode != 0)
        {
            return Finished(ModelLabExperimentStatus.Failed, state.WithLastOutput($"The trace script exited with status {exitCode}."));
        }

        var trace = new FileInfo(job.TracePath);
        var result = new FileInfo(job.ResultPath);
        if (!trace.Exists || !result.Exists)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The trace script finished without writing its results.");
        }

        // The script holds the cap itself. This is the check that does not depend on the script being right.
        if (trace.Length > job.TraceByteCap || result.Length > ModelLabTraceSpec.MaxResultBytes)
        {
            return Finished(ModelLabExperimentStatus.Failed, "The trace script wrote more than its byte cap, so nothing was kept.");
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

    private async Task SetReceiptAsync(Guid id, ReadOnlyMemory<byte> receipt)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, receipt, CancellationToken.None);
    }

    private string WorkDirectory(Guid id) =>
        Path.Combine(WorkRoot(_dataDirectory), id.ToString("N"));

    private static ModelLabExperimentCompletion Finished(ModelLabExperimentStatus status, string message) =>
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

        /// <summary>Written and read under the job lock.</summary>
        public bool RowCommitted { get; set; }
    }

    /// <summary>The instants are written by the output loop and a cancel, and read by the watchdog, so they are one word each.</summary>
    private sealed class StreamState
    {
        private const int OutputLinesKept = 12;
        private const int OutputLineLength = 300;

        // A native tool's own explanation reaches only stderr: a failed compile says why here and nowhere else.
        private readonly Queue<string> _lastOutput = new();
        private long _lastEventAtMs;
        private long _stopRequestedAtMs;

        public StreamState(DateTimeOffset startedAt)
        {
            StartedAt = startedAt;
            _lastEventAtMs = startedAt.ToUnixTimeMilliseconds();
        }

        public DateTimeOffset StartedAt { get; }

        public bool HandshakeSeen { get; set; }

        public bool Done { get; set; }

        public bool Cancelled { get; set; }

        public string? ErrorMessage { get; set; }

        public string? FailureReason { get; set; }

        public void KeepOutput(string line)
        {
            _lastOutput.Enqueue(line.Length > OutputLineLength ? line[..OutputLineLength] : line);
            if (_lastOutput.Count > OutputLinesKept)
            {
                _ = _lastOutput.Dequeue();
            }
        }

        public string WithLastOutput(string message) =>
            _lastOutput.Count == 0 ? message : $"{message}\n\nLast output:\n{string.Join('\n', _lastOutput)}";

        public DateTimeOffset LastEventAt() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _lastEventAtMs));

        public void MarkEvent(DateTimeOffset at) =>
            Volatile.Write(ref _lastEventAtMs, at.ToUnixTimeMilliseconds());

        public DateTimeOffset? StopRequestedAt() =>
            Volatile.Read(ref _stopRequestedAtMs) is var value and not 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;

        public void MarkStopRequested(DateTimeOffset at) =>
            Volatile.Write(ref _stopRequestedAtMs, at.ToUnixTimeMilliseconds());
    }
}
