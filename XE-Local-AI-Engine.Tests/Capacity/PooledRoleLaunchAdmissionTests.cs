namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="PooledRoleLaunchAdmission" /> tests: the pooled hook's verdict mapping and scope use, and the chat hook's
///     order (full tier, unload idle models, smaller window, refusal) and its no-op on a node with no GPU reading.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class PooledRoleLaunchAdmissionTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M";
    private const string ChatModel = "unsloth/Qwen3.6-35B-A3B-GGUF:UD-Q4_K_XL";
    private const string BusyModel = "'busy/chat-GGUF:Q4_K_M' (Chat)";
    private const string Reason ="Insufficient capacity for 'm' (Chat): not enough free memory for another model. Loaded now: 'x' (Chat). Eject one of them or pick a loaded model.";

    [Test]
    public async Task Allow_HandsTheReservationToTheCaller_AndOutlivesTheScope()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Allow(reservation));

        var returned = await host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned), "Allow must hand over the exact reservation the decision carried.");
        AssertEx.Equal(1, host.Created.Count);
        AssertEx.True(host.Created[0].Disposed, "The per-call scope must be disposed when the decision returns.");
        AssertEx.False(reservation.Disposed, "Disposing the scope must not release the reservation the supervisor now owns.");
    }

    [Test]
    public async Task QueueSameModel_HoldsNothing()
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.QueueSameModel,
            Reason = "Model already running; the spawn will share that process.",
            OllamaEvictionWarning = false
        });

        var returned = await host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None);

        AssertEx.Null(returned);
        AssertEx.Equal(1, host.Created.Count);
    }

    [Test]
    public async Task Reject_ThrowsTheSanitizedReason()
    {
        await using var host = Host.Create(Shortfall());

        var exception = await AssertEx.ThrowsAsync<LlamaCapacityRefusedException>(() => host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None));

        AssertEx.Equal(Reason, exception.Message);
    }

    [Test]
    [Arguments(ModelRole.Embedding, true)]
    [Arguments(ModelRole.Reranker, false)]
    public async Task OnlyTheEmbedder_IsNeverRejectedOnBudget(ModelRole role, bool expected)
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.QueueSameModel,
            Reason = "Model already running; the spawn will share that process.",
            OllamaEvictionWarning = false
        });

        await host.Admission.AdmitAsync(Model, role, CancellationToken.None);

        var request = AssertEx.NotNull(host.Created.Single().Requests.Single());
        AssertEx.Equal(Model, request.ModelName);
        AssertEx.Equal(role, request.Role);
        AssertEx.Equal(expected, request.NeverRejectOnBudget);
    }

    [Test]
    public async Task ChatRole_IsRefusedByThePooledEntryPoint()
    {
        await using var host = Host.Create(Allow(new TrackingDisposable()));

        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Admission.AdmitAsync(Model, ModelRole.Chat, CancellationToken.None));

        AssertEx.Empty(host.Created);
    }

    [Test]
    public async Task Chat_ThatFitsAtItsFullTier_IsAdmittedWithoutEvicting()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Allow(reservation));
        var evictions = new EvictionRecorder();

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned));
        var request = host.Created.Single().Requests.Single();
        AssertEx.Equal(ModelRole.Chat, request.Role);
        AssertEx.False(request.AllowAdmissionDownTier, "The first decision must not shrink the window before an idle model was unloaded.");
        AssertEx.True(request.SupervisorEnforcesProcessCap, "The supervisor's cap eviction owns the process count for its own launches.");
        AssertEx.Equal(0, evictions.Calls);
    }

    [Test]
    public async Task Chat_OnAMemoryShortfall_EvictsAnIdleModel_ThenIsAdmittedAtTheFullTier()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Shortfall(), Allow(reservation));
        host.KeepWarm("keep/warm-GGUF:Q4_K_M");
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned), "After the eviction the full-tier decision admits the model.");
        AssertEx.Equal(1, evictions.Calls);
        AssertEx.Equal("keep/warm-GGUF:Q4_K_M", evictions.Protected.Single().Single());
        var requests = host.Created.Single().Requests;
        AssertEx.Equal(2, requests.Count);
        AssertEx.False(requests[1].AllowAdmissionDownTier, "The re-decision after an eviction must still ask for the full tier.");
        await host.Audit.Received().GetEffectiveProfileAsync(forceRefreshProfile: true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Chat_WithNothingLeftToEvict_FallsToTheSmallerWindow()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Shortfall(), Allow(reservation));
        var evictions = new EvictionRecorder();

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned));
        AssertEx.Equal(1, evictions.Calls);
        AssertEx.Empty(evictions.Protected.Single());
        var requests = host.Created.Single().Requests;
        AssertEx.Equal(2, requests.Count);
        AssertEx.True(requests[1].AllowAdmissionDownTier, "With no victim left, the window may shrink.");
        AssertEx.Equal(TimeSpan.Zero, host.Time.Elapsed, "With nothing eligible, busy or not, the admission must not wait.");
    }

    [Test]
    public async Task Chat_WhenTheOnlyResidentIsBrieflyBusy_WaitsForIt_ThenEvictsAndAdmitsAtTheFullTier()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Shortfall(), Allow(reservation));
        var evictions = new EvictionRecorder(BusyModel)
        {
            BusyCalls = 3
        };

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned), "Once the resident went idle it is unloaded and the new model admitted, not refused.");
        AssertEx.Equal(4, evictions.Calls);
        var requests = host.Created.Single().Requests;
        AssertEx.Equal(2, requests.Count);
        AssertEx.False(requests[1].AllowAdmissionDownTier, "Waiting for the busy resident must buy the full tier, not a smaller window.");
        AssertEx.True(host.Time.Elapsed >= TimeSpan.FromSeconds(3), "The admission must have polled while the resident was busy.");
        AssertEx.True(host.Time.Elapsed < PooledRoleLaunchAdmission.BusyResidentWaitCap, "A resident that goes idle ends the wait early.");
    }

    [Test]
    public async Task Chat_WhenAResidentStaysBusyPastTheBound_FallsToTheSmallerWindow_ThenIsRefused()
    {
        await using var host = Host.Create(Shortfall(), Shortfall());
        var evictions = new EvictionRecorder
        {
            BusyCalls = int.MaxValue
        };

        var exception = await AssertEx.ThrowsAsync<LlamaCapacityRefusedException>(() => host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None));

        AssertEx.Equal(Reason, exception.Message);
        AssertEx.True(host.Time.Elapsed >= PooledRoleLaunchAdmission.BusyResidentWaitCap, "The wait must have run to its bound.");
        AssertEx.True(host.Time.Elapsed < PooledRoleLaunchAdmission.BusyResidentWaitCap + TimeSpan.FromSeconds(2), "The wait must stop at its bound.");
        var requests = host.Created.Single().Requests;
        AssertEx.Equal(2, requests.Count);
        AssertEx.True(requests[1].AllowAdmissionDownTier, "Only after the bound may the window shrink.");
    }

    [Test]
    public async Task Chat_CancelledWhileWaitingForABusyResident_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await using var host = Host.Create(Shortfall(), Allow(new TrackingDisposable()));
        var evictions = new EvictionRecorder
        {
            BusyCalls = int.MaxValue,
            OnCall = calls =>
            {
                if (calls == 2)
                {
                    cts.Cancel();
                }
            }
        };

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, cts.Token));

        AssertEx.Equal(2, evictions.Calls);
        AssertEx.Equal(1, host.Created.Single().Requests.Count);
    }

    [Test]
    public async Task Chat_WhenNoTierFitsEither_IsRefusedWithTheCapacityReason()
    {
        await using var host = Host.Create(Shortfall(), Shortfall());
        var evictions = new EvictionRecorder();

        var exception = await AssertEx.ThrowsAsync<LlamaCapacityRefusedException>(() => host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None));

        AssertEx.Equal(Reason, exception.Message);
        AssertEx.Equal(2, host.Created.Single().Requests.Count);
    }

    [Test]
    public async Task BackgroundChat_OnAMemoryShortfall_NeverEvictsOrWaits_TriesTheSmallerWindow_ThenIsRefused()
    {
        await using var host = Host.Create(Shortfall(), Shortfall());
        host.KeepWarm("keep/warm-GGUF:Q4_K_M");
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)")
        {
            BusyCalls = 1
        };

        var exception = await AssertEx.ThrowsAsync<LlamaCapacityRefusedException>(() => host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: false, evictions.EvictAsync, CancellationToken.None));

        AssertEx.Equal(Reason, exception.Message);
        AssertEx.Equal(0, evictions.Calls, "A background load must never unload the idle resident nor poll a busy one.");
        AssertEx.Equal(TimeSpan.Zero, host.Time.Elapsed, "A background load must not wait.");
        var requests = host.Created.Single().Requests;
        AssertEx.Equal(2, requests.Count);
        AssertEx.False(requests[0].AllowAdmissionDownTier);
        AssertEx.True(requests[1].AllowAdmissionDownTier, "The smaller window is still tried before the refusal.");
    }

    [Test]
    public async Task BackgroundChat_ThatFitsAtASmallerWindow_IsAdmittedWithoutEvicting()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Shortfall(), Allow(reservation));
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: false, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned));
        AssertEx.Equal(0, evictions.Calls);
        AssertEx.True(host.Created.Single().Requests[1].AllowAdmissionDownTier);
    }

    [Test]
    public async Task BackgroundChat_ThatFitsAtItsFullTier_IsAdmittedWithoutEvicting()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Allow(reservation));
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: false, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned));
        AssertEx.Equal(0, evictions.Calls);
        AssertEx.Equal(1, host.Created.Single().Requests.Count);
    }

    [Test]
    public async Task Chat_RejectThatEvictionCannotRelieve_IsRefusedWithoutEvicting()
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.RejectInsufficient,
            Reason = Reason,
            OllamaEvictionWarning = false
        });
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        await AssertEx.ThrowsAsync<LlamaCapacityRefusedException>(() => host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None));

        AssertEx.Equal(0, evictions.Calls);
        AssertEx.Equal(1, host.Created.Single().Requests.Count);
    }

    [Test]
    public async Task Chat_WhenTheFreedVramNeverShows_TheWaitIsBounded()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(Shortfall(), Allow(reservation));
        host.Audit.GetEffectiveProfileAsync(forceRefreshProfile: true, Arg.Any<CancellationToken>()).Returns(GpuProfile(free: 10 * Gb));
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned), "A reading that never rises still ends the wait and re-decides.");
        AssertEx.True(host.Time.Elapsed >= PooledRoleLaunchAdmission.FreedVramWaitCap, "The wait must have run to its cap.");
        AssertEx.True(host.Time.Elapsed < PooledRoleLaunchAdmission.FreedVramWaitCap + TimeSpan.FromSeconds(1), "The wait must stop at its cap.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Chat_OnANodeWithNoGlobalFreeVramReading_NeitherDecidesNorEvicts(bool gpuWithoutReading)
    {
        var profile = gpuWithoutReading ? GpuProfile(free: null) : CpuProfile();
        await using var host = Host.Create(profile, Shortfall());
        var evictions = new EvictionRecorder("'idle/chat-GGUF:Q4_K_M' (Chat)");

        var returned = await host.Admission.AdmitChatAsync(ChatModel, mayUnloadIdleModels: true, evictions.EvictAsync, CancellationToken.None);

        AssertEx.Null(returned);
        AssertEx.Empty(host.Created);
        AssertEx.Equal(0, evictions.Calls);
    }

    private static CapacityDecision Allow(IDisposable reservation) =>
        new()
        {
            Verdict = CapacityVerdict.Allow,
            Reason = "Capacity available.",
            OllamaEvictionWarning = false,
            Reservation = reservation
        };

    private static CapacityDecision Shortfall() =>
        new()
        {
            Verdict = CapacityVerdict.RejectInsufficient,
            Reason = Reason,
            OllamaEvictionWarning = false,
            IsMemoryShortfall = true
        };

    private static HardwareProfile GpuProfile(long? free) =>
        new()
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 32 * Gb,
            AvailableVramBytes = free,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };

    private static HardwareProfile CpuProfile() =>
        new()
        {
            TotalRamBytes = 32 * Gb,
            AvailableRamBytes = 16 * Gb,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 500 * Gb
        };

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Host(ServiceProvider provider, List<RecordingCapacityService> created, IRuntimeDeviceAudit audit, INodeRuntimeSettings settings)
        {
            _provider = provider;
            Created = created;
            Audit = audit;
            Settings = settings;
            Admission = new PooledRoleLaunchAdmission(provider.GetRequiredService<IServiceScopeFactory>(), Time, NullLogger<PooledRoleLaunchAdmission>.Instance);
        }

        public PooledRoleLaunchAdmission Admission { get; }

        public List<RecordingCapacityService> Created { get; }

        public IRuntimeDeviceAudit Audit { get; }

        public INodeRuntimeSettings Settings { get; }

        public SteppingTimeProvider Time { get; } = new();

        public static Host Create(params CapacityDecision[] decisions) =>
            Create(GpuProfile(free: 10 * Gb), decisions);

        public static Host Create(HardwareProfile profile, params CapacityDecision[] decisions)
        {
            var created = new List<RecordingCapacityService>();
            var queue = new Queue<CapacityDecision>(decisions);

            // A real node reports the memory a killed model released: the forced re-probe after an eviction reads more free VRAM.
            var audit = Substitute.For<IRuntimeDeviceAudit>();
            audit.GetEffectiveProfileAsync(forceRefreshProfile: false, Arg.Any<CancellationToken>()).Returns(profile);
            audit.GetEffectiveProfileAsync(forceRefreshProfile: true, Arg.Any<CancellationToken>())
                 .Returns(profile with
                 {
                     AvailableVramBytes = profile.AvailableVramBytes + 20 * Gb
                 });
            var settings = Substitute.For<INodeRuntimeSettings>();
            var services = new ServiceCollection();
            services.AddScoped<ICapacityService>(_ =>
            {
                var service = new RecordingCapacityService(queue);
                created.Add(service);
                return service;
            });
            services.AddSingleton(audit);
            services.AddSingleton(settings);
            return new Host(services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true
            }), created, audit, settings);
        }

        public void KeepWarm(string modelName)
        {
            Settings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
            Settings.GetKeepModelWarmModelNameAsync(Arg.Any<CancellationToken>()).Returns(modelName);
        }

        public ValueTask DisposeAsync() =>
            _provider.DisposeAsync();
    }

    // A hand-written capacity service: the verdicts are the input under test, in order (the last one repeats), and disposal proves the scope ended.
    private sealed class RecordingCapacityService : ICapacityService, IDisposable
    {
        private readonly Queue<CapacityDecision> _decisions;

        public RecordingCapacityService(Queue<CapacityDecision> decisions)
        {
            _decisions = decisions;
        }

        public List<CapacityRequest> Requests { get; } = [];

        public bool Disposed { get; private set; }

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            DecideAsync(new CapacityRequest
            {
                ModelName = modelName,
                Role = role
            }, ct);

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_decisions.Count > 1 ? _decisions.Dequeue() : _decisions.Peek());
        }

        public void Dispose() =>
            Disposed = true;
    }

    // The supervisor's eviction callback: reports a busy resident for the first BusyCalls calls, then hands out the given victims in order,
    // then reports nothing eligible.
    private sealed class EvictionRecorder
    {
        private readonly Queue<string> _victims;

        public EvictionRecorder(params string[] victims)
        {
            _victims = new Queue<string>(victims);
        }

        public int BusyCalls { get; init; }

        public Action<int>? OnCall { get; init; }

        public int Calls => Protected.Count;

        public List<IReadOnlyCollection<string>> Protected { get; } = [];

        public Task<IdleChatEvictionResult> EvictAsync(IReadOnlyCollection<string> protectedModels, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Protected.Add(protectedModels);
            OnCall?.Invoke(Calls);
            if (Calls <= BusyCalls)
            {
                return Task.FromResult(new IdleChatEvictionResult(EvictedModel: null, BusyModel));
            }

            return Task.FromResult(_victims.TryDequeue(out var victim) ? new IdleChatEvictionResult(victim, BusyModel: null) : IdleChatEvictionResult.NothingEligible);
        }
    }

    /// <summary>A clock whose timers fire at once and move the clock by their due time, so a bounded wait runs to its cap without real time passing.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _timestamp));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _timestamp, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() =>
            Disposed = true;
    }
}
