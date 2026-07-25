namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;

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
        using var service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            TimeProvider.System,
            NullLogger<KeepModelWarmBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await firstSettingsRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StopAsync(stopTimeout.Token);

        await resolver.DidNotReceiveWithAnyArgs().ResolveProviderForModelAsync(default!, default);
    }

    private static Harness CreateHarness(bool enabled, string? modelName, int intervalSeconds)
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var provider = Substitute.For<ILocalModelProvider>();
        var clock = new ManualTimeProvider();
        var harness = new Harness(resolver, provider, clock)
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

        harness.Service = new KeepModelWarmBackgroundService(runtimeSettings,
            resolver,
            clock,
            NullLogger<KeepModelWarmBackgroundService>.Instance);
        return harness;
    }

    private sealed class Harness(ILocalModelProviderResolver resolver,
        ILocalModelProvider provider,
        ManualTimeProvider clock)
    {
        public ManualTimeProvider Clock { get; } = clock;

        public bool Enabled { get; set; }

        public int IntervalSeconds { get; set; }

        public string? ModelName { get; set; }

        public ILocalModelProvider Provider { get; } = provider;

        public ILocalModelProviderResolver Resolver { get; } = resolver;

        public KeepModelWarmBackgroundService Service { get; set; } = null!;
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
}
