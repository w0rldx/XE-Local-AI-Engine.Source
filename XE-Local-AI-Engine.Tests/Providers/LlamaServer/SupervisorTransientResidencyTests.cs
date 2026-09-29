namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A process only a transient request (an AI Assist draft) loaded gets the short idle lifetime and yields its slot
///     under the cap; any interactive touch restores the normal lifetime, and an existing process is never marked.
/// </summary>
/// <remarks>A missed interactive touch would reap a model the operator is chatting with after two minutes, so every touch kind is pinned here.</remarks>
[Category(TestCategories.Unit)]
public sealed class SupervisorTransientResidencyTests
{
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan PastTransientTtl = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);

    [Test]
    public async Task TransientSpawn_IsReapedAfterTheShortTtl_WhileAnInteractiveProcessIsNot()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);

        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        var chatHandle = launcher.Handles.OrderBy(static handle => handle.ProcessId).First();
        var draftHandle = launcher.Handles.OrderBy(static handle => handle.ProcessId).Last();
        AssertEx.True(AssertEx.NotNull(supervisor.GetRegisteredProcess("draft-model", ModelRole.Chat)).IsTransient);
        AssertEx.False(AssertEx.NotNull(supervisor.GetRegisteredProcess("chat-model", ModelRole.Chat)).IsTransient);

        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(draftHandle.WasTreeKilled, "A transient process inside its short TTL must survive.");

        time.Advance(PastTransientTtl);
        await supervisor.ReapIdleOnceAsync();

        AssertEx.True(draftHandle.WasTreeKilled, "The transient process must be reaped once its short TTL elapsed.");
        AssertEx.False(chatHandle.WasTreeKilled, "An interactive process keeps the normal TTL.");
        AssertEx.Equal("chat-model", supervisor.ListRunningProcesses().Single().ModelName);
    }

    [Test]
    public async Task ProcessResidentBeforeATransientRequest_KeepsTheNormalTtl()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        // The draft borrows the already-loaded model: its ensure and its lease must not mark it.
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat, ModelResidencyIntent.Transient).Lease).Dispose();

        time.Advance(PastTransientTtl);
        await supervisor.ReapIdleOnceAsync();

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.False(AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat)).IsTransient);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A borrowed model must keep its normal lifetime.");
    }

    [Test]
    public async Task TransientTouches_KeepTheMark_AndRefreshTheShortTtl()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);

        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // A second draft reuses and leases the transient process: still transient, and its short TTL restarts.
        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient).Lease).Dispose();
        AssertEx.True(AssertEx.NotNull(supervisor.GetRegisteredProcess("draft-model", ModelRole.Chat)).IsTransient);

        time.Advance(TimeSpan.FromMinutes(1.5));
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The later transient touch restarted the short TTL.");

        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();
        AssertEx.True(launcher.Handles.Single().WasTreeKilled);
    }

    [Test]
    public async Task InteractiveEnsureReusingATransientProcess_ClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        await AssertKeepsNormalTtlAsync(supervisor, launcher, time);
    }

    [Test]
    public async Task InteractiveLeaseOnATransientProcess_ClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease).Dispose();

        await AssertKeepsNormalTtlAsync(supervisor, launcher, time);
    }

    [Test]
    public async Task KeepWarmOnATransientProcess_ClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        var provider = new LlamaServerLocalModelProvider(supervisor, new FakeModelStore(), time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        // KeepModelWarmBackgroundService warms through this provider call.
        await provider.WarmModelAsync("model-a", CancellationToken.None);

        await AssertKeepsNormalTtlAsync(supervisor, launcher, time);
    }

    [Test]
    public async Task InteractiveCallerJoiningATransientSpawn_LeavesAnInteractiveProcess()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedHealthProbe();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, probe, options: Options(cap: 3), timeProvider: time);

        var draft = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => probe.Waiting == 1, TimeSpan.FromSeconds(5), "The transient spawn never reached readiness.");
        var chat = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        probe.Release();
        await Task.WhenAll(draft, chat);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        await AssertKeepsNormalTtlAsync(supervisor, launcher, time);
    }

    [Test]
    public async Task TransientCallerCancelledDuringColdLoad_StillLeavesATransientProcess()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new GatedHealthProbe();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, probe, options: Options(cap: 3), timeProvider: time);
        using var cancellation = new CancellationTokenSource();

        var draft = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, cancellation.Token);
        await AssertEx.EventuallyAsync(() => probe.Waiting == 1, TimeSpan.FromSeconds(5), "The transient spawn never reached readiness.");
        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => draft);

        // The detached load finishes without its caller and registers the process the cancelled draft asked for.
        probe.Release();
        await AssertEx.EventuallyAsync(() => supervisor.GetRegisteredProcess("model-a", ModelRole.Chat) is not null,
            TimeSpan.FromSeconds(5),
            "The detached spawn never registered its process.");
        AssertEx.True(AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat)).IsTransient);

        time.Advance(PastTransientTtl);
        await supervisor.ReapIdleOnceAsync();
        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "A cancelled draft's model must still be reaped on the short TTL.");
    }

    [Test]
    public async Task CapFull_UnleasedInWindowTransientProcess_YieldsItsSlot_InteractiveChatDoesNot()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 2), timeProvider: time);

        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await supervisor.EnsureRunningAsync("chat-a", ModelRole.Chat, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));

        // Both are in-window; the interactive chat-a is the more valuable resident and must survive.
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);

        var handles = launcher.Handles.OrderBy(static handle => handle.ProcessId).ToList();
        AssertEx.Equal(expected: 3, handles.Count);
        AssertEx.True(handles[0].WasTreeKilled, "The in-window transient process yields its slot.");
        AssertEx.False(handles[1].WasTreeKilled, "An in-window interactive chat process is never a cap victim.");
    }

    [Test]
    public async Task CapFull_LeasedTransientProcess_IsNotAVictim()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 1), timeProvider: time);

        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient).Lease);

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None));
        AssertEx.Contains(ex.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "A draft mid-generation must never be evicted.");

        // Even past the short TTL, the leased draft is not reaped.
        time.Advance(PastTransientTtl);
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The reaper must never kill a leased process.");

        lease.Dispose();
        AssertEx.True(AssertEx.NotNull(supervisor.GetRegisteredProcess("draft-model", ModelRole.Chat)).IsTransient,
            "The draft's own lease must not clear the mark.");
        await supervisor.EnsureRunningAsync("chat-b", ModelRole.Chat, CancellationToken.None);
        AssertEx.True(launcher.Handles.OrderBy(static handle => handle.ProcessId).First().WasTreeKilled);
    }

    [Test]
    [Arguments(ModelResidencyIntent.Interactive)]
    [Arguments(ModelResidencyIntent.Transient)]
    public async Task LeaseOutlastingTheTtl_IdleTimeRunsFromTheRelease_AndTheMarkIsUnchanged(ModelResidencyIntent intent)
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        var ttl = intent == ModelResidencyIntent.Transient ? TimeSpan.FromMinutes(2) : IdleTtl;
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, intent, CancellationToken.None);
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat, intent).Lease);

        time.Advance(ttl + TimeSpan.FromMinutes(1));
        lease.Dispose();
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "Idle time must run from the release, not from the start of the request.");
        AssertEx.Equal(intent == ModelResidencyIntent.Transient, AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat)).IsTransient);

        time.Advance(ttl - TimeSpan.FromSeconds(1));
        await supervisor.ReapIdleOnceAsync();
        AssertEx.False(launcher.Handles.Single().WasTreeKilled);

        time.Advance(TimeSpan.FromSeconds(1));
        await supervisor.ReapIdleOnceAsync();
        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "One full lifetime after the release the process is idle.");
    }

    [Test]
    public async Task LeaseDisposedTwice_ReleasesAndStampsOnce()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var running = AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat));
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("model-a", ModelRole.Chat).Lease);

        time.Advance(TimeSpan.FromMinutes(1));
        lease.Dispose();
        var releasedAt = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(1));
        lease.Dispose();

        AssertEx.Equal(releasedAt, running.LastUsedUtc);
        AssertEx.Equal(expected: 0, running.ActiveLeases);
    }

    [Test]
    public async Task TransientTtlLongerThanTheIdleTtl_IsClampedToTheIdleTtl()
    {
        var options = new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = 3,
            IdleTimeToLive = TimeSpan.FromMinutes(1),
            MaxRestartAttempts = 3
        };
        AssertEx.Equal(TimeSpan.FromMinutes(1), options.EffectiveTransientIdleTimeToLive);

        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: options, timeProvider: time);
        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));
        await supervisor.ReapIdleOnceAsync();

        AssertEx.True(launcher.Handles.Single().WasTreeKilled, "A transient process never outlives the normal idle TTL.");
    }

    [Test]
    public async Task HealthSnapshot_ReportsBusyLastUseAndTransient_OnTheWireDto()
    {
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        await using var supervisor = SupervisorFactory.Create(launcher, options: Options(cap: 3), timeProvider: time);
        await supervisor.EnsureRunningAsync("chat-model", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("draft-model", ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("chat-model", ModelRole.Chat).Lease);
        var leasedAt = time.GetUtcNow();

        var rows = (await supervisor.CheckHealthAsync(CancellationToken.None)).Select(static health => health.ToResponse()).ToDictionary(static row => row.ModelName);

        AssertEx.True(rows["chat-model"].IsBusy);
        AssertEx.False(rows["chat-model"].IsTransient);
        AssertEx.Equal<DateTimeOffset?>(leasedAt, rows["chat-model"].LastUsedUtc);
        AssertEx.False(rows["draft-model"].IsBusy);
        AssertEx.True(rows["draft-model"].IsTransient);
    }

    [Test]
    public async Task TransientChatClient_EnsuresWithTransientIntent_InteractiveClientWithThePlainOverload()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var endpoint = new LlamaServerEndpoint
        {
            ModelName = "model-a",
            Role = ModelRole.Chat,
            BaseAddress = new Uri("http://127.0.0.1:19001/v1")
        };
        supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, Arg.Any<CancellationToken>()).Returns(endpoint);
        supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, Arg.Any<CancellationToken>()).Returns(endpoint);
        var provider = new LlamaServerLocalModelProvider(supervisor, new FakeModelStore(), TimeProvider.System);

        using var transient = (DeferredLlamaServerChatClient)provider.CreateChatClient(Selection(ModelResidencyIntent.Transient));
        await transient.ResolveEndpointAsync(CancellationToken.None);
        _ = supervisor.Received(1).EnsureRunningAsync("model-a", ModelRole.Chat, ModelResidencyIntent.Transient, Arg.Any<CancellationToken>());
        _ = supervisor.DidNotReceive().EnsureRunningAsync("model-a", ModelRole.Chat, Arg.Any<CancellationToken>());

        using var interactive = (DeferredLlamaServerChatClient)provider.CreateChatClient(Selection(ModelResidencyIntent.Interactive));
        await interactive.ResolveEndpointAsync(CancellationToken.None);
        _ = supervisor.Received(1).EnsureRunningAsync("model-a", ModelRole.Chat, Arg.Any<CancellationToken>());
    }

    private static LocalModelSelection Selection(ModelResidencyIntent intent)
    {
        return new LocalModelSelection
        {
            ModelName = "model-a",
            ProviderName = LlamaServerProviderConstants.ProviderName,
            ResidencyIntent = intent
        };
    }

    /// <summary>The single "model-a" process is no longer transient and survives well past the short TTL.</summary>
    private static async Task AssertKeepsNormalTtlAsync(LlamaServerProcessSupervisor supervisor, FakeProcessLauncher launcher, AdvanceableTimeProvider time)
    {
        AssertEx.False(AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat)).IsTransient,
            "An interactive touch must clear the transient mark.");

        time.Advance(PastTransientTtl + TimeSpan.FromMinutes(5));
        await supervisor.ReapIdleOnceAsync();

        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "An interactively used model must keep the normal TTL.");
    }

    private static LlamaServerSupervisorOptions Options(int cap)
    {
        return new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = cap,
            IdleTimeToLive = IdleTtl,
            MaxRestartAttempts = 3
        };
    }
}
