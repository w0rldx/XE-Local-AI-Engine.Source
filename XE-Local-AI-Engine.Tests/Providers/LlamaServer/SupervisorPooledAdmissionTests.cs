namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pooled-role capacity admission at the supervisor: a cold embedding / reranker spawn asks the
///     <see cref="ILlamaServerPooledLaunchAdmission" /> hook once and holds its reservation until the spawn settles.
/// </summary>
/// <remarks>
///     The reservation binds the launch in the real <see cref="ProcessLaunchAdmissionRegistry" />, as the application's
///     capacity gate does. Released before the launch ticket, it would orphan the registry entry: a global blocker that
///     makes every capacity decision reject.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SupervisorPooledAdmissionTests
{
    private const string Reranker = "bge-reranker";
    private const string NonRetryableMarker = "LlamaServer.NonRetryable";

    [Test]
    public async Task ConcurrentColdPooledEnsures_AskTheHookOnce_AndLaunchOnce()
    {
        var launcher = new FakeProcessLauncher();
        var gatedProbe = new GatedHealthProbe();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry);
        await using var supervisor = Create(launcher, registry, hook, gatedProbe);

        var ensures = Enumerable.Range(0, 4)
                                .Select(_ => supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None))
                                .ToArray();
        await AssertEx.EventuallyAsync(() => gatedProbe.Waiting == 1, TimeSpan.FromSeconds(5), "the one spawn should park in readiness");
        gatedProbe.Release();
        await Task.WhenAll(ensures).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Equal(expected: 1, hook.Calls, "N concurrent cold ensures of one pooled key must decide admission once.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task ColdChatEnsure_AsksTheChatHookOnce_AndAResidentChatNever()
    {
        var launcher = new FakeProcessLauncher();
        var hook = new RecordingPooledAdmission(registry: null);
        await using var supervisor = Create(launcher, new ProcessLaunchAdmissionRegistry(), hook);

        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount, "the second ensure must reuse the resident process");
        AssertEx.Equal(expected: 1, hook.ChatCalls, "A cold chat spawn is admitted once; a turn on a resident model is never admitted.");
        AssertEx.Equal(expected: 0, hook.Calls, "Chat goes through the chat entry point, not the pooled one.");
    }

    [Test]
    [Arguments(ModelResidencyIntent.Interactive, true)]
    [Arguments(ModelResidencyIntent.Transient, true)]
    [Arguments(ModelResidencyIntent.Background, false)]
    public async Task ColdChatEnsure_OnlyABackgroundLoad_MayNotUnloadIdleModels(ModelResidencyIntent intent, bool expected)
    {
        var hook = new RecordingPooledAdmission(registry: null);
        await using var supervisor = Create(new FakeProcessLauncher(), new ProcessLaunchAdmissionRegistry(), hook);

        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, intent, CancellationToken.None);

        AssertEx.Equal(expected, hook.MayUnloadIdleModels.Single());
    }

    [Test]
    public async Task RefusedBackgroundLoad_LeavesNoSpawnToJoin_SoAUserTurnIsAdmittedOnItsOwn()
    {
        var launcher = new FakeProcessLauncher();
        var hook = new RecordingPooledAdmission(registry: null)
        {
            RefuseBackground = true
        };
        await using var supervisor = Create(launcher, new ProcessLaunchAdmissionRegistry(), hook);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None));
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal("False,True", string.Join(",", hook.MayUnloadIdleModels));
        AssertEx.Equal(expected: 1, launcher.LaunchCount, "The user turn runs its own admission and loads; the background refusal never binds it.");
    }

    [Test]
    public async Task ChatEnsure_WithAnAdmissionAlreadyPublished_SkipsTheHook_AndConsumesIt()
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry);
        await using var supervisor = Create(launcher, registry, hook);

        // A sub-agent dispatch decided on its own and published the admission this launch is expected to consume.
        AssertEx.True(registry.TryAcquire(RecordingPooledAdmission.Admission("chat-model", ModelRole.Chat), out var consumer));
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        consumer!.Dispose();

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(expected: 0, hook.ChatCalls, "A second decision would reject on the admission its own caller published.");
        AssertEx.False(registry.Snapshot("chat-model", ModelRole.Chat).HasRequestedKey, "The published admission must have been consumed and released.");
    }

    [Test]
    public async Task ChatHookRefusal_FailsNonRetryable_WithoutLaunching()
    {
        var launcher = new FakeProcessLauncher();
        var hook = new RecordingPooledAdmission(registry: null)
        {
            Refusal = new LlamaRuntimeException("Insufficient capacity for 'chat-model' (Chat). Eject one of them or pick a loaded model.")
        };
        await using var supervisor = Create(launcher, new ProcessLaunchAdmissionRegistry(), hook);

        var refused = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None));

        AssertEx.True(refused.Data.Contains(NonRetryableMarker), "A refused chat load is a policy outcome and must be classified non-retryable.");
        AssertEx.Contains(refused.Message, "Eject one of them", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
    }

    [Test]
    public async Task WarmPooledReuse_DoesNotAskTheHookAgain()
    {
        var hook = new RecordingPooledAdmission(registry: null);
        await using var supervisor = Create(new FakeProcessLauncher(), new ProcessLaunchAdmissionRegistry(), hook);

        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);
        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);

        AssertEx.Equal(expected: 1, hook.Calls, "A warm reuse has nothing to admit.");
        AssertEx.Equal(ModelRole.Embedding, hook.Roles.Single());
    }

    [Test]
    public async Task HookRefusal_FailsNonRetryable_WithoutLaunching()
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry)
        {
            Refusal = new LlamaRuntimeException("Not enough free GPU memory to load the reranker.")
        };
        await using var supervisor = Create(launcher, registry, hook);

        var refused = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None));

        AssertEx.Equal(expected: 1, hook.Calls);
        AssertEx.True(refused.Data.Contains(NonRetryableMarker), "A capacity refusal is a policy outcome and must be classified non-retryable.");
        AssertEx.Equal(expected: 0, launcher.LaunchCount, "A refused pooled launch must never reach the launcher.");
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
        AssertEx.False(registry.Snapshot("other", ModelRole.Chat).HasGlobalBlocker, "A refusal leaves no registry entry behind.");
    }

    [Test]
    public async Task Reservation_IsReleasedAfterTheLaunchTicket_WhenTheSpawnBecomesReady()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry);
        await using var supervisor = Create(new FakeProcessLauncher(), registry, hook);

        await supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None);

        var reservation = hook.Reservations.Single();
        AssertEx.True(reservation.Disposed, "The reservation lives only until the spawn settles.");
        AssertEx.False(reservation.GlobalBlockerAtRelease, "Released before the ticket, the reservation orphans the entry: a global blocker.");
        AssertEx.False(registry.Snapshot("other", ModelRole.Chat).HasGlobalBlocker);
        AssertEx.False(registry.Snapshot(Reranker, ModelRole.Reranker).HasRequestedKey);
    }

    [Test]
    public async Task Reservation_IsReleasedAfterTheLaunchTicket_WhenTheSpawnFails()
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry);

        // No installed file: the spawn fails deterministically after the hook admitted it.
        await using var supervisor = Create(launcher, registry, hook, modelStore: new FakeModelStore(fixedPath: null));

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None));

        var reservation = hook.Reservations.Single();
        AssertEx.True(reservation.Disposed, "A failed spawn must release its reservation too.");
        AssertEx.False(reservation.GlobalBlockerAtRelease);
        AssertEx.False(registry.Snapshot("other", ModelRole.Chat).HasGlobalBlocker);
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    public async Task BackgroundChatLoad_AdmittedOnMemory_ButRefusedAtTheCap_ReleasesItsReservation()
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = new RecordingPooledAdmission(registry);
        await using var supervisor = Create(launcher, registry, hook, options: new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = 1,
            IdleTimeToLive = TimeSpan.FromHours(1)
        });
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        var refused = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None));

        AssertEx.Contains(refused.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.True(refused.Data.Contains(NonRetryableMarker), "the cap refusal is a policy outcome, like the capacity refusal");
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "the in-window transient chat process is not a background victim");
        var reservation = hook.Reservations.Last();
        AssertEx.Equal(expected: 2, hook.Reservations.Count);
        AssertEx.True(reservation.Disposed, "the memory admission's reservation is released when the spawn is refused at the cap");
        AssertEx.False(reservation.GlobalBlockerAtRelease);
        AssertEx.False(registry.Snapshot("chat-b", ModelRole.Chat).HasRequestedKey, "no ledger booking is left behind");
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    public async Task ReapThenEnsure_AsksTheHookAgain()
    {
        var launcher = new FakeProcessLauncher();
        var hook = new RecordingPooledAdmission(registry: null);
        await using var supervisor = Create(launcher, new ProcessLaunchAdmissionRegistry(), hook);

        await supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None);
        await supervisor.EvictAsync(Reranker, ModelRole.Reranker, CancellationToken.None);
        await supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount, "the evicted reranker must have been spawned again");
        AssertEx.Equal(expected: 2, hook.Calls, "Every cold launch is admitted, including one after a reap.");
    }

    private static LlamaServerProcessSupervisor Create(FakeProcessLauncher launcher,
        ProcessLaunchAdmissionRegistry registry,
        RecordingPooledAdmission hook,
        ILlamaServerHealthProbe? healthProbe = null,
        FakeModelStore? modelStore = null,
        LlamaServerSupervisorOptions? options = null)
    {
        return SupervisorFactory.Create(launcher,
            healthProbe,
            modelStore,
            options,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry,
            pooledLaunchAdmission: hook);
    }

    /// <summary>
    ///     Counts admissions and, with a registry, binds each one there the way the capacity gate publishes an admission.
    /// </summary>
    private sealed class RecordingPooledAdmission : ILlamaServerPooledLaunchAdmission
    {
        private readonly ProcessLaunchAdmissionRegistry? _registry;
        private int _calls;
        private int _chatCalls;

        public RecordingPooledAdmission(ProcessLaunchAdmissionRegistry? registry)
        {
            _registry = registry;
        }

        public Exception? Refusal { get; init; }

        public bool RefuseBackground { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public int ChatCalls => Volatile.Read(ref _chatCalls);

        public ConcurrentQueue<ModelRole> Roles { get; } = new();

        public ConcurrentQueue<bool> MayUnloadIdleModels { get; } = new();

        public ConcurrentQueue<Reservation> Reservations { get; } = new();

        public Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Roles.Enqueue(role);
            if (Refusal is not null)
            {
                throw Refusal;
            }

            if (_registry is null)
            {
                return Task.FromResult<IDisposable?>(null);
            }

            AssertEx.True(_registry.TryAcquire(Admission(modelName, role), out var consumer), "the test admission must publish");
            var reservation = new Reservation(_registry, consumer!);
            Reservations.Enqueue(reservation);
            return Task.FromResult<IDisposable?>(reservation);
        }

        public Task<IDisposable?> AdmitChatAsync(string modelName,
            bool mayUnloadIdleModels,
            Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>> evictIdleModel,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _chatCalls);
            MayUnloadIdleModels.Enqueue(mayUnloadIdleModels);
            if (Refusal is not null)
            {
                throw Refusal;
            }

            if (RefuseBackground && !mayUnloadIdleModels)
            {
                throw new LlamaCapacityRefusedException("Insufficient capacity for 'chat-model' (Chat). Eject one of them or pick a loaded model.");
            }

            if (_registry is null)
            {
                return Task.FromResult<IDisposable?>(null);
            }

            AssertEx.True(_registry.TryAcquire(Admission(modelName, ModelRole.Chat), out var consumer), "the test admission must publish");
            var reservation = new Reservation(_registry, consumer!);
            Reservations.Enqueue(reservation);
            return Task.FromResult<IDisposable?>(reservation);
        }

        public static ProcessLaunchAdmission Admission(string modelName, ModelRole role) =>
            new()
            {
                ModelName = modelName,
                Role = role,
                Variant = GpuVariant.Cpu,
                ResolvedArguments = ResolvedLaunchArguments.Explore(),
                Allocation = new ProcessContextAllocation
                {
                    ProcessContextTokens = 2048,
                    ModelTrainContextTokens = 8192,
                    Source = ProcessContextAllocationSource.HardwareTier,
                    Placement = ProcessPlacementMode.Cpu,
                    Footprint = ResourceFootprint.Zero,
                    ContentIdentity = $"{modelName}:0",
                    CacheKey = $"cache:{modelName}"
                }
            };
    }

    /// <summary>A consumer lease that records, as it is released, whether that release left a global blocker behind.</summary>
    private sealed class Reservation : IDisposable
    {
        private readonly ProcessLaunchAdmissionRegistry _registry;
        private readonly IProcessLaunchAdmissionLease _consumer;

        public Reservation(ProcessLaunchAdmissionRegistry registry, IProcessLaunchAdmissionLease consumer)
        {
            _registry = registry;
            _consumer = consumer;
        }

        public bool Disposed { get; private set; }

        public bool GlobalBlockerAtRelease { get; private set; }

        public void Dispose()
        {
            _consumer.Dispose();
            GlobalBlockerAtRelease = _registry.Snapshot("other", ModelRole.Chat).HasGlobalBlocker;
            Disposed = true;
        }
    }
}
