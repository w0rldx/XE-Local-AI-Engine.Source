namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The one owner of a Model Lab worker's GPU holds: the exclusive gate, the llama lease, the reservation, the process and its receipt file.</summary>
/// <remarks>
///     A job borrows the session and hands it on when it ends: to Idle, to eviction, or for a steering job's arms to the job. Holds are never
///     released and taken again, because the lease gate is not reentrant and a queued ensure would win the gap. Every transition happens under
///     one lock that is never held across an await; the eviction runs once, on a task of its own that no requester's token can cancel.
/// </remarks>
internal sealed class ModelLabGpuSession : IGpuIdleHolder
{
    private readonly GpuIdleHolderBroker _broker;
    private readonly TimeProvider _idleClock;
    private readonly Func<bool> _llamaGateHasWaiters;
    private readonly ILogger _logger;
    private readonly Action<string, byte[]?, TrainingCapacityReservation?> _orphaned;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IRuntimeResidencyChangeNotifier _residency;
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private IDisposable? _gate;
    private long _idleGeneration;
    private ITimer? _idleTimer;
    private ILlamaServerRuntimeMutationLease? _lease;
    private bool _noReuse;
    private bool _preemptionPending;
    private byte[]? _receipt;
    private string? _receiptPath;
    private TrainingCapacityReservation? _reservation;
    private ModelLabSessionState _state = ModelLabSessionState.Admitting;
    private ModelLabServeWorker? _worker;

    /// <param name="modelKey">The model the worker holds: a warm start needs the same key.</param>
    /// <param name="baseArtifactId">The checkpoint, so its delete can evict an idle worker first.</param>
    /// <param name="repoId">The checkpoint's repository, which the residents row shows.</param>
    /// <param name="services">The broker, the residency notifier, the clocks and the bounds.</param>
    /// <param name="orphaned">Gets an unverified worker's receipt file, its receipt as held in memory, and the reservation kept for it.</param>
    public ModelLabGpuSession(string modelKey,
        Guid baseArtifactId,
        string repoId,
        ModelLabGpuSessionServices services,
        Action<string, byte[]?, TrainingCapacityReservation?> orphaned)
    {
        ArgumentNullException.ThrowIfNull(services);
        ModelKey = modelKey;
        BaseArtifactId = baseArtifactId;
        RepoId = repoId;
        _broker = services.Broker;
        _idleClock = services.IdleClock;
        _llamaGateHasWaiters = services.LlamaGateHasWaiters;
        _logger = services.Logger;
        _residency = services.Residency;
        _timeProvider = services.TimeProvider;
        _orphaned = orphaned;
    }

    public string ModelKey { get; }

    public Guid BaseArtifactId { get; }

    public string RepoId { get; }

    public ModelLabSessionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>The process, for the job that borrows the session; null until its first job spawns one.</summary>
    public ModelLabServeWorker? Worker
    {
        get
        {
            lock (_sync)
            {
                return _worker;
            }
        }
    }

    /// <summary>The residents row: none once the session is leaving.</summary>
    public ModelLabWarmResident? Resident
    {
        get
        {
            RuntimeResidentState? state;
            ModelLabServeWorker? worker;
            lock (_sync)
            {
                state = _state switch
                {
                    ModelLabSessionState.Admitting => RuntimeResidentState.Starting,
                    ModelLabSessionState.Active => RuntimeResidentState.Active,
                    ModelLabSessionState.Idle => RuntimeResidentState.Idle,
                    _ => null
                };
                worker = _worker;
            }

            // Read outside the session's lock, so the worker's lock never nests inside it.
            return state is { } shown
                ? new ModelLabWarmResident
                {
                    State = shown,
                    ModelId = RepoId,
                    VramBytes = worker?.VramBytes
                }
                : null;
        }
    }

    public bool IsIdle => State == ModelLabSessionState.Idle;

    /// <summary>Joins the broker. Called while admitting, before the gate is taken, so demand that races the admission is latched.</summary>
    /// <remarks>No residency tick: the Starting row exists only once the owner publishes this session, and the owner ticks then.</remarks>
    public void Register() =>
        _broker.Register(this);

    /// <summary>A cold admission took all three holds: the session owns them and a job borrows it from here.</summary>
    public void Admitted(IDisposable gate, ILlamaServerRuntimeMutationLease lease, TrainingCapacityReservation reservation)
    {
        lock (_sync)
        {
            _gate = gate;
            _lease = lease;
            _reservation = reservation;
            _state = ModelLabSessionState.Active;
        }

        _residency.NotifyChanged();
    }

    /// <summary>A cold admission failed; the caller has already given back whatever it took.</summary>
    public void Abandon() =>
        End();

    /// <summary>The job's spawn: the session owns the process and its receipt file from here, so even a failure right after kills it.</summary>
    public void AttachWorker(ModelLabServeWorker worker, string receiptPath)
    {
        ArgumentNullException.ThrowIfNull(worker);
        worker.OnBroken = OnWorkerBroken;
        // Held in memory as well: the file may never get written, and only the receipt can still find the process.
        var receipt = ModelLabWarmReceipts.Serialize(worker.Receipt);
        lock (_sync)
        {
            _worker = worker;
            _receiptPath = receiptPath;
            _receipt = receipt;
        }
    }

    /// <summary>A warm start: from Idle, with the same model, and only while nothing has asked for the session to go.</summary>
    /// <remarks>No residency tick: the owner claims under its own lock and ticks once it has published the job.</remarks>
    public bool TryEnterActive(string modelKey)
    {
        ITimer? timer;
        lock (_sync)
        {
            if (_state != ModelLabSessionState.Idle || _noReuse || !string.Equals(modelKey, ModelKey, StringComparison.Ordinal))
            {
                return false;
            }

            _state = ModelLabSessionState.Active;
            _idleGeneration++;
            timer = _idleTimer;
            _idleTimer = null;
        }

        timer?.Dispose();
        return true;
    }

    /// <summary>The job let go. Idle for <paramref name="idleFor" /> when it may keep the worker; otherwise, or with demand latched, evicting.</summary>
    /// <returns>The eviction, or a completed task when the session went idle.</returns>
    public Task ExitActive(bool keepWarm, TimeSpan idleFor)
    {
        bool idle;
        long generation;
        lock (_sync)
        {
            if (_state != ModelLabSessionState.Active)
            {
                return _released.Task;
            }

            // A gate waiter's demand may predate this session's registration and so never have latched.
            idle = keepWarm && idleFor > TimeSpan.Zero && _worker is not null && !_preemptionPending && !_noReuse && !_llamaGateHasWaiters();
            _state = idle ? ModelLabSessionState.Idle : ModelLabSessionState.Evicting;
            generation = ++_idleGeneration;
        }

        _residency.NotifyChanged();
        if (!idle)
        {
            StartEviction();
            return _released.Task;
        }

#pragma warning disable CA2000 // Disposed by whichever transition leaves this idle generation.
        var timer = _idleClock.CreateTimer(_ => OnIdleExpired(generation), state: null, idleFor, Timeout.InfiniteTimeSpan);
#pragma warning restore CA2000
        var stale = false;
        lock (_sync)
        {
            if (_state == ModelLabSessionState.Idle && _idleGeneration == generation)
            {
                _idleTimer = timer;
            }
            else
            {
                stale = true;
            }
        }

        if (stale)
        {
            timer.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>Nothing may reuse the worker any more: the job's end evicts it. For a host stop.</summary>
    public void ForbidReuse()
    {
        lock (_sync)
        {
            _noReuse = true;
        }
    }

    /// <summary>Evicts an idle session and waits for the holds to come back; <see langword="false" /> at once while a job holds it.</summary>
    /// <remarks>A refusal still latches the request, so the job runs on uninterrupted and its end evicts instead of idling.</remarks>
    public async Task<bool> EvictAsync(CancellationToken ct)
    {
        bool start;
        lock (_sync)
        {
            if (_state is ModelLabSessionState.Admitting or ModelLabSessionState.Active)
            {
                _preemptionPending = true;
                return false;
            }

            _noReuse = true;
            start = TryBeginEvictingLocked();
        }

        if (start)
        {
            _residency.NotifyChanged();
            StartEviction();
        }

        await _released.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>A steering job's process-only eviction: the worker stops, the reservation goes, and the gate and lease pass to the job for its arms.</summary>
    /// <returns>The gate and lease, or <see langword="null" /> when the exit was not seen and the holds went back as an eviction gives them.</returns>
    public async Task<ModelLabArmsHolds?> DetachForArmsAsync()
    {
        ModelLabServeWorker? worker;
        lock (_sync)
        {
            if (_state != ModelLabSessionState.Active)
            {
                return null;
            }

            _state = ModelLabSessionState.Evicting;
            worker = _worker;
        }

        _residency.NotifyChanged();
        var verified = await StopWorkerAsync(worker);
        IDisposable? gate;
        ILlamaServerRuntimeMutationLease? lease;
        TrainingCapacityReservation? reservation;
        string? receiptPath;
        lock (_sync)
        {
            gate = _gate;
            lease = _lease;
            reservation = _reservation;
            receiptPath = _receiptPath;
            _gate = null;
            _lease = null;
            _reservation = null;
            _worker = null;
        }

        if (verified && gate is not null && lease is not null)
        {
            DeleteReceipt(receiptPath);
            reservation?.Dispose();
            // Ended now, so a one-retry caller refuses at once while the arms run instead of waiting out its bound.
            End();
            return new ModelLabArmsHolds
            {
                Gate = gate,
                Lease = lease
            };
        }

        try
        {
            // The orphan hand-over comes first, so a start that sees this session released re-reaps the kept receipt.
            await ReleaseUnverifiedAsync(verified, receiptPath, reservation, lease, gate);
        }
        finally
        {
            End();
        }

        return null;
    }

    /// <inheritdoc />
    public void RequestPreemption()
    {
        bool start;
        lock (_sync)
        {
            if (_state is ModelLabSessionState.Admitting or ModelLabSessionState.Active)
            {
                _preemptionPending = true;
                return;
            }

            start = TryBeginEvictingLocked();
        }

        if (start)
        {
            _residency.NotifyChanged();
            StartEviction();
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_state is ModelLabSessionState.Admitting or ModelLabSessionState.Active)
            {
                _preemptionPending = true;
                return false;
            }
        }

        try
        {
            await _released.Task.WaitAsync(bound, _timeProvider, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void OnIdleExpired(long generation)
    {
        bool start;
        lock (_sync)
        {
            start = _idleGeneration == generation && TryBeginEvictingLocked();
        }

        if (start)
        {
            _residency.NotifyChanged();
            StartEviction();
        }
    }

    /// <summary>The worker broke or exited: an idle session goes at once, a running job's end evicts it.</summary>
    private void OnWorkerBroken()
    {
        bool start;
        lock (_sync)
        {
            _noReuse = true;
            start = TryBeginEvictingLocked();
        }

        if (start)
        {
            _residency.NotifyChanged();
            StartEviction();
        }
    }

    /// <summary>Under the lock: Idle moves to Evicting, and the idle timer's generation is retired. False from any other state.</summary>
    private bool TryBeginEvictingLocked()
    {
        if (_state != ModelLabSessionState.Idle)
        {
            return false;
        }

        _state = ModelLabSessionState.Evicting;
        _idleGeneration++;
        _idleTimer?.Dispose();
        _idleTimer = null;
        return true;
    }

    private void StartEviction() =>
        _ = Task.Run(EvictCoreAsync, CancellationToken.None);

    /// <summary>The order: stop the worker; once its exit is seen, the receipt file, the reservation, the lease, then the gate.</summary>
    private async Task EvictCoreAsync()
    {
        ModelLabServeWorker? worker;
        lock (_sync)
        {
            worker = _worker;
        }

        var verified = await StopWorkerAsync(worker);
        IDisposable? gate;
        ILlamaServerRuntimeMutationLease? lease;
        TrainingCapacityReservation? reservation;
        string? receiptPath;
        lock (_sync)
        {
            gate = _gate;
            lease = _lease;
            reservation = _reservation;
            receiptPath = _receiptPath;
            _gate = null;
            _lease = null;
            _reservation = null;
            _worker = null;
        }

        try
        {
            if (verified)
            {
                DeleteReceipt(receiptPath);
                reservation?.Dispose();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                gate?.Dispose();
            }
            else
            {
                await ReleaseUnverifiedAsync(verified, receiptPath, reservation, lease, gate);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Giving back the holds of the Model Lab worker for {RepoId} failed.", RepoId);
        }
        finally
        {
            End();
        }
    }

    /// <summary>An exit that was not seen: the reservation and the receipt file stay so the ledger and the next start know; the lease and gate go.</summary>
    private async Task ReleaseUnverifiedAsync(bool verified,
        string? receiptPath,
        TrainingCapacityReservation? reservation,
        ILlamaServerRuntimeMutationLease? lease,
        IDisposable? gate)
    {
        if (!verified && receiptPath is not null)
        {
            _logger.LogError(
                "The Model Lab worker for {RepoId} was not seen to exit, so it may still hold GPU memory. Its reservation and receipt are kept, and Model Lab refuses new starts until it is gone.",
                RepoId);
            byte[]? receipt;
            lock (_sync)
            {
                receipt = _receipt;
            }

            _orphaned(receiptPath, receipt, reservation);
        }
        else
        {
            DeleteReceipt(receiptPath);
            reservation?.Dispose();
        }

        if (lease is not null)
        {
            await lease.DisposeAsync();
        }

        gate?.Dispose();
    }

    /// <summary>Stops the worker; its scratch directory goes only once its exit is seen, since a live process may still write there.</summary>
    private async Task<bool> StopWorkerAsync(ModelLabServeWorker? worker)
    {
        if (worker is null)
        {
            return true;
        }

        try
        {
            var exited = await worker.StopAsync();
            if (exited)
            {
                BestEffortFile.TryDeleteDirectory(worker.WorkingDirectory);
            }

            return exited;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Stopping the Model Lab worker for {RepoId} failed.", RepoId);
            return false;
        }
        finally
        {
            worker.Dispose();
        }
    }

    private static void DeleteReceipt(string? receiptPath)
    {
        if (receiptPath is not null)
        {
            BestEffortFile.TryDeleteFile(receiptPath);
        }
    }

    /// <summary>The session is over: Ended, deregistered, released. Every hand-over of what it held has already happened.</summary>
    private void End()
    {
        lock (_sync)
        {
            _state = ModelLabSessionState.Ended;
        }

        _broker.Deregister(this);
        _released.TrySetResult();
        _residency.NotifyChanged();
    }
}
