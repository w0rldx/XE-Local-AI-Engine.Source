namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class KeepModelWarmBackgroundServiceTests
{
    [Test]
    public async Task RunIterationAsync_WhenDisabled_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: false, modelName: "model-a", intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenEnabled_WarmsConfiguredModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.Received(1).ResolveProviderForModelAsync("model-a", Arg.Any<CancellationToken>());
        await harness.Provider.Received(1).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenDisabledAfterWarm_StopsTouchingWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Enabled = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(1).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenSettingsChange_UsesNewModelWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.ModelName = "model-b";
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(1).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
        await harness.Provider.Received(1).WarmModelAsync("model-b", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenIntervalChanges_UsesNewCadenceWithoutRestart()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 300);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(120));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(1).WarmModelAsync("model-a", Arg.Any<CancellationToken>());

        harness.IntervalSeconds = 60;
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(2).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenIntervalElapses_TouchesResidentModelAgain()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        await harness.Service.RunIterationAsync(CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        // The provider's idempotent warm path reuses the resident process and refreshes its idle timestamp.
        await harness.Provider.Received(2).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenWarmFails_RetriesAfterConfiguredInterval()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.Provider.WarmModelAsync("model-a", Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("load failed")), Task.CompletedTask);

        await harness.Service.RunIterationAsync(CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.Received(2).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenWarmFailsRepeatedly_LogsOneWarningUntilSuccess()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        var shouldFail = true;
        harness.Provider.WarmModelAsync("model-a", Arg.Any<CancellationToken>())
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
        harness.Provider.WarmModelAsync("model-a", Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new LlamaRuntimeException("spawn failed")));

        await harness.Service.RunIterationAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.Logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        AssertEx.Equal(expected: 1, harness.Logger.Entries.Count(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("llama.cpp model", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunIterationAsync_WhenMutationEnds_ResetsFailureBudget()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.Provider.WarmModelAsync("model-a", Arg.Any<CancellationToken>())
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

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenRuntimeMutationIsSuppressed_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.RuntimeMutationSuppressed = true;

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default);
    }

    [Test]
    public async Task RunIterationAsync_WhenSourceBuildIsActive_DoesNotWarmModel()
    {
        var harness = CreateHarness(enabled: true, modelName: "model-a", intervalSeconds: 60);
        harness.SourceBuildActive = true;

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Provider.DidNotReceiveWithAnyArgs().WarmModelAsync(default!, default);
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
        await harness.Provider.Received(1).WarmModelAsync("model-a", Arg.Any<CancellationToken>());

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Service.RunIterationAsync(CancellationToken.None);
        await harness.Provider.Received(2).WarmModelAsync("model-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunIterationAsync_WhenEnabledWithoutModel_DoesNotResolveProvider()
    {
        var harness = CreateHarness(enabled: true, modelName: null, intervalSeconds: 300);

        await harness.Service.RunIterationAsync(CancellationToken.None);

        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
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

        harness.Service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            supervisor,
            sourceBuildActivity,
            new LlamaServerSupervisorOptions
            {
                MaxLoadedProcesses = maxLoadedProcesses,
                IdleTimeToLive = idleTimeToLive ?? TimeSpan.FromMinutes(15)
            },
            clock,
            logger);
        return harness;
    }

    private sealed class Harness(
        ILocalModelProviderResolver resolver,
        ILocalModelProvider provider,
        ILlamaServerProcessSupervisor supervisor,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        ManualTimeProvider clock,
        RecordingLogger<KeepModelWarmBackgroundService> logger)
    {
        public ManualTimeProvider Clock { get; } = clock;

        public bool Enabled { get; set; }

        public int IntervalSeconds { get; set; }

        public string? ModelName { get; set; }

        public ILocalModelProvider Provider { get; } = provider;

        public ILocalModelProviderResolver Resolver { get; } = resolver;

        public bool RuntimeMutationSuppressed { get; set; }

        public KeepModelWarmBackgroundService Service { get; set; } = null!;

        public bool SourceBuildActive { get; set; }

        public ILlamaServerProcessSupervisor Supervisor { get; } = supervisor;

        public ILlamaCppSourceBuildActivity SourceBuildActivity { get; } = sourceBuildActivity;

        public RecordingLogger<KeepModelWarmBackgroundService> Logger { get; } = logger;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public void Advance(TimeSpan interval)
        {
            _timestamp += interval.Ticks;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
        }

        public sealed record Entry(LogLevel Level, string Message);
    }
}
