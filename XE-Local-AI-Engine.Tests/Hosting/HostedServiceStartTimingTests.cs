namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Start timing on a real generic host: the markers keep the start order and the concrete types, and a slow
///     service is named before ApplicationStarted. The clock moves only when a fake service spends time.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class HostedServiceStartTimingTests
{
    [Test]
    public async Task Install_KeepsTheStartOrder_AndLogsTheSlowServiceAndASummaryBeforeApplicationStarted()
    {
        var time = new AdvanceableTimeProvider();
        var logger = new CapturingLogger();
        var order = new List<string>();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        _ = builder.Services.AddSingleton<TimeProvider>(time);
        _ = builder.Services.AddSingleton<ILogger<HostedServiceStartTiming>>(logger);
        // Plain singletons: AddHostedService would collapse the two SpendingService registrations into one.
        _ = builder.Services.AddSingleton<IHostedService>(_ => new SpendingService("first", TimeSpan.FromMilliseconds(40), order, time));
        _ = builder.Services.AddHostedService(_ => new SlowStartService(order, time));
        _ = builder.Services.AddSingleton<IHostedService>(_ => new SpendingService("third", TimeSpan.Zero, order, time));

        HostedServiceStartTiming.Install(builder.Services);

        using var host = builder.Build();
        string[]? linesAtStarted = null;
        _ = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => linesAtStarted = logger.Lines);
        await host.StartAsync();
        await host.StopAsync();

        AssertEx.Equal("first,SlowStartService,third", string.Join(',', order), "The markers must not change the start order.");
        AssertEx.Equal(expected: 1, host.Services.GetServices<IHostedService>().OfType<SlowStartService>().Count(),
            "A hosted service must still resolve as its own type.");
        var lines = AssertEx.NotNull(linesAtStarted, "ApplicationStarted must have fired.");
        AssertEx.Equal(expected: 2, lines.Length, string.Join(" | ", lines));
        AssertEx.Equal("Hosted service SlowStartService started in 150 ms.", lines[0]);
        AssertEx.Equal("Hosted services started in 190 ms (3 services)", lines[1]);
    }

    private sealed class SpendingService : IHostedService
    {
        private readonly TimeSpan _cost;
        private readonly string _name;
        private readonly List<string> _order;
        private readonly AdvanceableTimeProvider _time;

        public SpendingService(string name, TimeSpan cost, List<string> order, AdvanceableTimeProvider time)
        {
            _name = name;
            _cost = cost;
            _order = order;
            _time = time;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _order.Add(_name);
            _time.AdvanceTimestamp(_cost);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Its own type, so the slow line can name it.</summary>
    private sealed class SlowStartService : IHostedService
    {
        private readonly List<string> _order;
        private readonly AdvanceableTimeProvider _time;

        public SlowStartService(List<string> order, AdvanceableTimeProvider time)
        {
            _order = order;
            _time = time;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _order.Add(nameof(SlowStartService));
            _time.AdvanceTimestamp(TimeSpan.FromMilliseconds(150));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<HostedServiceStartTiming>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
