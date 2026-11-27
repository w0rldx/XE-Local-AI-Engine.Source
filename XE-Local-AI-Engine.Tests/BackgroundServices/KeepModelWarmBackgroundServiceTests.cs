namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Capacity;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class KeepModelWarmBackgroundServiceTests
{
    [Test]
    public async Task RunIterationAsync_WhenDisabled_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: false, modelName: "model-a", intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenEnabled_WarmsConfiguredModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.Received(1).ResolveProviderForModelAsync("model-a", Arg.Any<CancellationToken>());
        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenDisabledAfterWarm_StopsTouchingWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Enabled = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenSettingsChange_UsesNewModelWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.ModelName = "model-b";
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
        await harness.Provider.Received(1).WarmModelAsync("model-b", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenIntervalChanges_UsesNewCadenceWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(120));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());

        harness.IntervalSeconds = 60;
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(2).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenIntervalElapses_TouchesResidentModelAgain()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        // The provider's idempotent warm path reuses the resident process and refreshes its idle timestamp.
        await harness.Provider.Received(2).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenWarmFails_RetriesAfterConfiguredInterval()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.Provider.WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("load failed")), Task.CompletedTask);

        await harness.Service.RunIterationAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(2).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenWarmFailsRepeatedly_LogsOneWarningUntilSuccess()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        var shouldFail = true;
        harness.Provider.WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>())
               .Returns(_ => shouldFail
                   ? Task.FromException(new InvalidOperationException("load failed"))
                   : Task.CompletedTask);

        await harness.Service.RunIterationAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("iteration failed", StringComparison.Ordinal)));
        AssertEx.Equal(expected: 1, harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("iteration failed", StringComparison.Ordinal)));

        shouldFail = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        shouldFail = true;
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("iteration failed", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunIterationAsync_WhenLlamaRuntimeFails_LeavesWarningOwnershipWithSupervisor()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.Provider.WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new LlamaRuntimeException("spawn failed")));

        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        AssertEx.Equal(expected: 1, harness.Logger.Entries.Count(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("llama.cpp model", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Through the real provider and supervisor: an operator-ejected model is refused at every due iteration, the supervisor
    ///     owns the one Information line, and keep-warm stays at Debug on its cadence.
    /// </summary>
    [Test]
    public async Task RunIterationAsync_AfterAnOperatorEject_SkipsEveryDueIterationAtDebug_AndTheSupervisorLogsOnce()
    {
        var supervisorLogger = new RecordingLogger<LlamaServerProcessSupervisor>();
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, logger: supervisorLogger);
        AssertEx.Equal(LlamaServerEjectOutcome.NotRunning, await supervisor.EjectAsync("model-a", ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: true));
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        runtimeSettings.GetKeepModelWarmModelNameAsync(Arg.Any<CancellationToken>()).Returns("model-a");
        runtimeSettings.GetKeepModelWarmIntervalAsync(Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(60));
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync("model-a", Arg.Any<CancellationToken>())
                .Returns(new LlamaServerLocalModelProvider(supervisor, new FakeModelStore(), TimeProvider.System));
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger<KeepModelWarmBackgroundService>();
        using var service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            supervisor,
            Substitute.For<ILlamaCppSourceBuildActivity>(),
            new LlamaServerSupervisorOptions
            {
                MaxLoadedProcesses = 3,
                IdleTimeToLive = TimeSpan.FromMinutes(15)
            },
            clock,
            logger);

        await service.RunIterationAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.RunIterationAsync(CancellationToken.None);

        var refusals = supervisorLogger.Entries.Where(static entry => entry.Message.Contains("the operator ejected it", StringComparison.Ordinal)).Select(static entry => entry.Level);
        AssertEx.Equal("Information,Debug", string.Join(",", refusals), "Both due iterations reach the supervisor; only the first refusal is at Information.");
        AssertEx.Equal(expected: 2, logger.Entries.Count(static entry => entry.Level == LogLevel.Debug && entry.Message.Contains("llama.cpp model", StringComparison.Ordinal)));
        AssertEx.False(logger.Entries.Any(static entry => entry.Level >= LogLevel.Information), logger.AllText);
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
    }

    [Test]
    public async Task RunIterationAsync_WhenMutationEnds_ResetsFailureBudget()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.Provider.WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("load failed")));

        await harness.Service.RunIterationAsync(CancellationToken.None);
        harness.RuntimeMutationSuppressed = true;
        await harness.Service.RunIterationAsync(CancellationToken.None);
        harness.RuntimeMutationSuppressed = false;
        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Logger.Entries.Count(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("iteration failed", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunIterationAsync_WhenActiveProcessCapIsOne_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60, maxLoadedProcesses: 1);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default, default);
    }

    /// <summary>The cap is a live setting: the warning names the fix without a restart.</summary>
    [Test]
    public async Task RunIterationAsync_WhenActiveProcessCapIsOne_WarnsToRaiseTheCap_WithoutARestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60, maxLoadedProcesses: 1);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.ContainsSingle(harness.Logger.Entries,
            static entry => entry.Level == LogLevel.Warning
                            && entry.Message == "Keep model warm is enabled, but the active llama.cpp process cap is 1; at least two slots are required. Raise the cap to at least 2.");
        AssertEx.False(harness.Logger.AllText.Contains("restart", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task RunIterationAsync_WhenTheCapIsRaisedLive_StartsWarming()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60, maxLoadedProcesses: 1);
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default, default);

        harness.SupervisorOptions.MaxLoadedProcesses = 2;
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenRuntimeMutationIsSuppressed_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.RuntimeMutationSuppressed = true;

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenSourceBuildIsActive_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.SourceBuildActive = true;

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenConfiguredIntervalExceedsActiveTtl_UsesHalfActiveTtl()
    {
        var harness = CreateHarness(enabled: true,
            modelName: "model-a",
            intervalSeconds: 300,
            idleTimeToLive: TimeSpan.FromSeconds(30));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(14));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(1).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(2).WarmModelAsync("model-a", ModelResidencyIntent.Background, Arg.Any<CancellationToken>());
        AssertEx.ContainsSingle(harness.Logger.Entries,
            static entry => entry.Level == LogLevel.Warning
                            && entry.Message == "Keep model warm interval 00:05:00 is too close to the active llama.cpp idle TTL 00:00:30; using 00:00:15 instead.");
        AssertEx.False(harness.Logger.AllText.Contains("restart", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task RunIterationAsync_WhenEnabledWithoutModel_DoesNotResolveProvider()
    {
        var harness = CreateHarness(enabled: true, modelName: null, intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
    }

    /// <summary>
    ///     The only periodic ensure must never evict an idle Model Lab worker: the worker's lease suppresses it on the REAL
    ///     supervisor, so the iteration returns before any ensure and the worker survives its whole idle time.
    /// </summary>
    [Test]
    public async Task RunIterationAsync_WhileAnIdleGpuHolderHoldsTheLease_RaisesNoDemand()
    {
        await using var holders = new IdleHolderHarness();
        var holder = await holders.AdmitIdleAsync();
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        runtimeSettings.GetKeepModelWarmModelNameAsync(Arg.Any<CancellationToken>()).Returns("llama3");
        runtimeSettings.GetKeepModelWarmIntervalAsync(Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(60));
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        using var service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            holders.Supervisor,
            Substitute.For<ILlamaCppSourceBuildActivity>(),
            new LlamaServerSupervisorOptions
            {
                // Two slots, so the iteration reaches the suppression check instead of stopping at the capacity warning.
                MaxLoadedProcesses = 3
            },
            new ManualTimeProvider(),
            NullLogger<KeepModelWarmBackgroundService>.Instance);
        AssertEx.True(holders.Supervisor.IsKeepWarmSuppressed(), "The idle worker's lease is what suppresses keep-warm.");

        await service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, holder.PreemptionRequestCount, "Keep-warm raised demand against the idle worker.");
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Idle, holder.State);
        await resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
        AssertEx.Equal(expected: 0, holders.Launcher.LaunchCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenHostStops_CancelsPendingTimerCleanly()
    {
        var firstSettingsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           firstSettingsRead.TrySetResult();
                           return false;
                       });
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var sourceBuildActivity = Substitute.For<ILlamaCppSourceBuildActivity>();
        using var service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            supervisor,
            sourceBuildActivity,
            new LlamaServerSupervisorOptions(),
            TimeProvider.System,
            NullLogger<KeepModelWarmBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await firstSettingsRead.Task.WaitAsync(TestBudgets.Contended);
        using var stopTimeout = new CancellationTokenSource(TestBudgets.Contended);

        await service.StopAsync(stopTimeout.Token);

        await resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
    }

    private static Harness CreateHarness(bool enabled,
        string? modelName,
        int intervalSeconds,
        int maxLoadedProcesses = 3,
        TimeSpan? idleTimeToLive = null)
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var provider = Substitute.For<ILocalModelProvider>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var sourceBuildActivity = Substitute.For<ILlamaCppSourceBuildActivity>();
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger<KeepModelWarmBackgroundService>();
        var harness = new Harness(resolver, provider, supervisor, sourceBuildActivity, clock, logger)
        {
            Enabled = enabled,
            ModelName = modelName,
            IntervalSeconds = intervalSeconds
        };

        runtimeSettings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => harness.Enabled);
        runtimeSettings.GetKeepModelWarmModelNameAsync(Arg.Any<CancellationToken>()).Returns(_ => harness.ModelName);
        runtimeSettings.GetKeepModelWarmIntervalAsync(Arg.Any<CancellationToken>())
                       .Returns(_ => TimeSpan.FromSeconds(harness.IntervalSeconds));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        provider.ProviderName.Returns("llamacpp");
        supervisor.IsKeepWarmSuppressed().Returns(_ => harness.RuntimeMutationSuppressed);
        sourceBuildActivity.ActiveBuildId.Returns(_ => harness.SourceBuildActive ? Guid.Parse("58fce305-7417-4a9f-a9c3-058567492496") : (Guid?)null);

        harness.SupervisorOptions = new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = maxLoadedProcesses,
            IdleTimeToLive = idleTimeToLive ?? TimeSpan.FromMinutes(15)
        };
        harness.Service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            supervisor,
            sourceBuildActivity,
            harness.SupervisorOptions,
            clock,
            logger);
        return harness;
    }

    private sealed class Harness
    {
        public Harness(ILocalModelProviderResolver resolver,
            ILocalModelProvider provider,
            ILlamaServerProcessSupervisor supervisor,
            ILlamaCppSourceBuildActivity sourceBuildActivity,
            ManualTimeProvider clock,
            RecordingLogger<KeepModelWarmBackgroundService> logger)
        {
            Clock = clock;
            Provider = provider;
            Resolver = resolver;
            Service = null!;
            Supervisor = supervisor;
            SourceBuildActivity = sourceBuildActivity;
            Logger = logger;
        }

        public ManualTimeProvider Clock { get; }

        public bool Enabled { get; set; }

        public int IntervalSeconds { get; set; }

        public string? ModelName { get; set; }

        public ILocalModelProvider Provider { get; }

        public ILocalModelProviderResolver Resolver { get; }

        public bool RuntimeMutationSuppressed { get; set; }

        public KeepModelWarmBackgroundService Service { get; set; }

        public LlamaServerSupervisorOptions SupervisorOptions { get; set; } = null!;

        public bool SourceBuildActive { get; set; }

        public ILlamaServerProcessSupervisor Supervisor { get; }

        public ILlamaCppSourceBuildActivity SourceBuildActivity { get; }

        public RecordingLogger<KeepModelWarmBackgroundService> Logger { get; }
    }
}
