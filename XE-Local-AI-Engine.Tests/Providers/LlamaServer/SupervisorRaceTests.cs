namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that concurrent ensure-running for the same <c>(model, role)</c> spawns exactly once (single-flight
///     gate), and a model the reaper evicted mid-tool-call is transparently re-spawned on the next ensure-running
///     (restart, not failure).
/// </summary>
public sealed class SupervisorRaceTests
{
    [Test]
    public async Task DisposeAsync_WaitsForAdmittedEjectBeforeDisposingMutationState()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = SupervisorFactory.Create(launcher,
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                MaxRestartAttempts = 3,
                EjectDrainTimeout = TimeSpan.FromSeconds(5)
            });
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var inferenceLease = supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease;
        AssertEx.NotNull(inferenceLease);

        var eject = supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None);
        AssertEx.True(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).ProcessEvicting,
            "The eject must synchronously mark the process evicting before entering its drain wait.");
        var disposal = supervisor.DisposeAsync().AsTask();
        await AssertEx.StaysIncompleteAsync(disposal, "Disposal must wait for the already-admitted eject operation.");

        inferenceLease!.Dispose();
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, await eject.WaitAsync(TimeSpan.FromSeconds(3)));
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.True(launcher.Handles.Single().WasTreeKilled);
    }

    [Test]
    public async Task PublicAsyncMutators_AfterDispose_RejectBeforeTouchingDisposedGates()
    {
        var supervisor = SupervisorFactory.Create();
        await supervisor.DisposeAsync();

        await AssertEx.ThrowsAsync<ObjectDisposedException>(() =>
            supervisor.EvictAsync("model-a", ModelRole.Chat, CancellationToken.None));
        await AssertEx.ThrowsAsync<ObjectDisposedException>(() =>
            supervisor.EvictAllRolesAsync("model-a", CancellationToken.None));
        await AssertEx.ThrowsAsync<ObjectDisposedException>(() =>
            supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None));
    }

    [Test]
    public async Task RuntimeMutationLease_WhileHeld_BlocksEnsureUntilDisposed()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);

        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(ensure, "An ensure must not be admitted while a runtime mutation lease is held.");
        AssertEx.Equal(0, launcher.LaunchCount);

        await (lease ?? throw new InvalidOperationException("lease must not be null.")).DisposeAsync();
        await ensure;
        AssertEx.Equal(1, launcher.LaunchCount);
    }

    [Test]
    public async Task DisposeAsync_RejectsEnsureThatPassedEntryCheckBeforeMutationBarrier()
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var supervisor = SupervisorFactory.Create(launcher, launchAdmissions: registry);
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);
        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(ensure, "An ensure must not be admitted while a runtime mutation lease is held.");

        var disposal = supervisor.DisposeAsync().AsTask();
        await AssertEx.StaysIncompleteAsync(disposal, "Disposal must wait behind the mutation lease the ensure is parked on.");
        await (lease ?? throw new InvalidOperationException("lease must not be null.")).DisposeAsync();

        await AssertEx.ThrowsAsync<ObjectDisposedException>(() => ensure);
        await disposal;
        AssertEx.Equal(0, launcher.LaunchCount);
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
        AssertEx.False(registry.Snapshot("model-a", ModelRole.Chat).HasRequestedKey);
    }

    [Test]
    public async Task DisposeAsync_CancelsMutationLeaseDelayedBeforeRuntimeGateWithoutActivityLeak()
    {
        var supervisor = SupervisorFactory.Create();
        var blocker = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(blocker);
        var pending = supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(pending, "A second mutation lease must not be granted while the first is held.");
        AssertEx.True(supervisor.IsKeepWarmSuppressed());

        var disposal = supervisor.DisposeAsync().AsTask();
        await AssertEx.StaysIncompleteAsync(disposal, "Disposal must wait behind the mutation lease the pending acquire is parked on.");
        await (blocker ?? throw new InvalidOperationException("blocker must not be null.")).DisposeAsync();

        await AssertEx.ThrowsAsync<ObjectDisposedException>(() => pending);
        await disposal;
        AssertEx.False(supervisor.IsKeepWarmSuppressed());
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    public async Task RuntimeMutationLease_WhenProcessRunning_FailsWithoutLeavingSuppressionBehind()
    {
        await using var supervisor = SupervisorFactory.Create();
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);

        AssertEx.Null(lease);
        AssertEx.False(supervisor.IsKeepWarmSuppressed());
    }

    [Test]
    public async Task RuntimeMutationLease_WhileHeld_SuppressesKeepWarmUntilDisposed()
    {
        await using var supervisor = SupervisorFactory.Create();

        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);

        AssertEx.NotNull(lease);
        AssertEx.True(supervisor.IsKeepWarmSuppressed());

        await (lease ?? throw new InvalidOperationException("lease must not be null.")).DisposeAsync();
        AssertEx.False(supervisor.IsKeepWarmSuppressed());
    }

    [Test]
    public async Task RuntimeMutationLease_CancelledPendingAttemptReleasesItsSuppression()
    {
        await using var supervisor = SupervisorFactory.Create();
        var ownedLease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(ownedLease);
        using var pendingCancellation = new CancellationTokenSource();
        var pendingAttempt = supervisor.TryAcquireRuntimeMutationLeaseAsync(pendingCancellation.Token);

        await AssertEx.StaysIncompleteAsync(pendingAttempt, "The pending acquire must still be queued behind the owned lease.");
        AssertEx.True(supervisor.IsKeepWarmSuppressed());

        await pendingCancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => pendingAttempt);
        AssertEx.True(supervisor.IsKeepWarmSuppressed());

        await (ownedLease ?? throw new InvalidOperationException("ownedLease must not be null.")).DisposeAsync();
        AssertEx.False(supervisor.IsKeepWarmSuppressed());
    }

    [Test]
    public async Task RuntimeMutationLease_CancelledWaitAndDoubleDispose_DoNotCorruptOrderingGate()
    {
        await using var supervisor = SupervisorFactory.Create();
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            supervisor.EnsureRunningAsync("cancelled", ModelRole.Chat, cancelled.Token));

        await (lease ?? throw new InvalidOperationException("lease must not be null.")).DisposeAsync();
        await lease.DisposeAsync();
        var endpoint = await supervisor.EnsureRunningAsync("healthy", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(endpoint);
    }

    [Test]
    public async Task EnsureRunning_CallerCancellation_RetainsOrphanBlockerUntilDetachedLaunchSettles()
    {
        var health = new GatedHealthProbe();
        var registry = new ProcessLaunchAdmissionRegistry();
        var allocation = new ProcessContextAllocation(8192,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            ContentIdentity: "model-a:0",
            CacheKey: "cache:model-a");
        using var consumer = registry.Acquire(new ProcessLaunchAdmission("model-a",
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            allocation));
        AssertEx.NotNull(consumer);
        await using var supervisor = SupervisorFactory.Create(healthProbe: health,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry);
        using var cancellation = new CancellationTokenSource();
        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, cancellation.Token);
        await WaitUntilAsync(() => health.Waiting == 1);

        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ensure);
        consumer!.Dispose();
        AssertEx.True(registry.Snapshot("model-b", ModelRole.Chat).HasGlobalBlocker);
        AssertEx.False(registry.TryAcquire(new ProcessLaunchAdmission("model-b",
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            allocation with
            {
                ContentIdentity = "model-b:0",
                CacheKey = "cache:model-b"
            }), out _));

        health.Release();
        await WaitUntilAsync(() => !registry.Snapshot("model-a", ModelRole.Chat).HasRequestedKey);
    }

    [Test]
    public async Task DisposeAsync_AwaitsDetachedSpawnCleanupAndReleasesLaunchTicket()
    {
        var health = new GatedHealthProbe();
        var launcher = new FakeProcessLauncher();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var registry = new ProcessLaunchAdmissionRegistry();
        var allocation = new ProcessContextAllocation(8192,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.Cpu,
            ResourceFootprint.Zero,
            ContentIdentity: "model-a:0",
            CacheKey: "cache:model-a");
        AssertEx.True(registry.TryAcquire(new ProcessLaunchAdmission("model-a",
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            allocation), out var consumer));
        var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: health,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry,
            loadTelemetry: telemetry);
        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await WaitUntilAsync(() => health.Waiting == 1);
        consumer!.Dispose();

        await supervisor.DisposeAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ensure);
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
        var handle = launcher.Handles.Single();
        AssertEx.True(handle.WasTreeKilled);
        AssertEx.True(handle.WasDisposed);
        var observations = telemetry.Observations.ToArray();
        AssertEx.Equal(expected: 1, observations.Length);
        AssertEx.Equal(LlamaServerReadinessOutcome.Cancelled, observations[0].Outcome);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observations[0].AttemptKind);
        AssertEx.False(registry.Snapshot("model-a", ModelRole.Chat).HasRequestedKey);
        AssertEx.True(registry.TryAcquire(new ProcessLaunchAdmission("model-b",
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            allocation with
            {
                ContentIdentity = "model-b:0",
                CacheKey = "cache:model-b"
            }), out var next));
        next!.Dispose();
    }

    [Test]
    public async Task DisposeAsync_WhenDetachedSpawnIsQueuedButNotStarted_StillRunsCleanup()
    {
        var scheduler = new ManualTaskScheduler();
        var registry = new ProcessLaunchAdmissionRegistry();
        var supervisor = SupervisorFactory.Create(launchAdmissions: registry,
            detachedSpawnScheduler: scheduler);

        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 1, scheduler.PendingCount);
        AssertEx.Equal(expected: 1, supervisor.CountInflightSpawns());

        var disposal = supervisor.DisposeAsync().AsTask();
        AssertEx.False(disposal.IsCompleted,
            "Shutdown must wait for the queued detached spawn to publish its cancellation and release its ticket.");

        scheduler.RunNext();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => ensure);
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
        AssertEx.False(registry.Snapshot("model-a", ModelRole.Chat).HasRequestedKey);
    }

    private static Task WaitUntilAsync(Func<bool> predicate) =>
        AssertEx.EventuallyAsync(predicate, TestBudgets.Contended, "Timed out waiting for the expected detached-spawn state.");

    [Test]
    public async Task EnsureRunning_SourceBuildReserved_FailsWithoutSpawn()
    {
        var launcher = new FakeProcessLauncher();
        ILlamaCppSourceBuildActivity activity = new LlamaCppSourceBuildActivity();
        AssertEx.True(activity.TryReserve(Guid.NewGuid()));
        await using var supervisor = SupervisorFactory.Create(launcher, sourceBuildActivity: activity);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(exception.Message, "source build", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(0, launcher.LaunchCount);
        AssertEx.Equal(0, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task EnsureRunning_ConcurrentSameKey_SpawnsExactlyOnce()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var calls = Enumerable.Range(start: 0, count: 20)
                              .Select(_ => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None))
                              .ToArray();
        var endpoints = await Task.WhenAll(calls);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        var first = endpoints[0].BaseAddress.AbsoluteUri;
        AssertEx.True(endpoints.All(e => string.Equals(e.BaseAddress.AbsoluteUri, first, StringComparison.Ordinal)));
    }

    [Test]
    public async Task EnsureRunning_AfterEvictionMidToolCall_RespawnsInsteadOfFailing()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var first = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        await supervisor.EvictAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var second = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.NotNull(second);
        AssertEx.True(second.BaseAddress.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal));
        AssertEx.Equal("127.0.0.1", first.BaseAddress.Host);
    }

    [Test]
    public async Task EnsureRunning_AfterProcessCrash_RespawnsOnNextRequest()
    {
        // First launch yields a handle we crash; subsequent launches yield fresh healthy handles.
        var spawnCount = 0;
#pragma warning disable CA2000 // Ownership transfers to the supervisor (via the launcher fake), which disposes it on teardown.
        var crashHandle = new FakeProcessHandle(2000);
#pragma warning restore CA2000
        var launcher = new FakeProcessLauncher(_ =>
            Interlocked.Increment(ref spawnCount) == 1 ? crashHandle : new FakeProcessHandle(4000));
        await using var supervisor = SupervisorFactory.Create(launcher);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        crashHandle.SimulateExit(); // the process dies between requests.

        var afterCrash = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount); // dead process detected → respawned.
        AssertEx.NotNull(afterCrash);
    }
}

internal sealed class ManualTaskScheduler : TaskScheduler
{
    private readonly Queue<Task> _scheduled = new();

    public int PendingCount
    {
        get
        {
            lock (_scheduled)
            {
                return _scheduled.Count;
            }
        }
    }

    public void RunNext()
    {
        Task task;
        lock (_scheduled)
        {
            task = _scheduled.Dequeue();
        }

        if (!TryExecuteTask(task))
        {
            throw new InvalidOperationException("The queued detached-spawn task could not be executed.");
        }
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        lock (_scheduled)
        {
            return _scheduled.ToArray();
        }
    }

    protected override void QueueTask(Task task)
    {
        lock (_scheduled)
        {
            _scheduled.Enqueue(task);
        }
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        false;
}
