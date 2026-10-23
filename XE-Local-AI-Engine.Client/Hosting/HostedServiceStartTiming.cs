namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Times each hosted service's <c>StartAsync</c> with one shared marker registered before every hosted service and
///     after the last. The host starts them in order, so the time between two marker starts is the service between them.
/// </summary>
/// <remarks>
///     Markers rather than a wrapper: the host stops on a failed <see cref="BackgroundService" /> only when it sees that
///     type, and callers find hosted services by their concrete type. Logs each service at or over
///     <see cref="SlowThreshold" /> and one summary line, both before <c>ApplicationStarted</c>.
/// </remarks>
internal sealed class HostedServiceStartTiming : IHostedService
{
    /// <summary>A hosted service whose <c>StartAsync</c> takes at least this long is logged by type name.</summary>
    internal static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<HostedServiceStartTiming> _logger;
    private readonly int _markerCount;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private int _calls;
    private long _firstTimestamp;
    private IHostedService[]? _hostedServices;
    private long _previousTimestamp;

    public HostedServiceStartTiming(IServiceProvider services, ILogger<HostedServiceStartTiming> logger, TimeProvider timeProvider, int markerCount)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _services = services;
        _logger = logger;
        _timeProvider = timeProvider;
        _markerCount = markerCount;
    }

    /// <summary>Places the markers; call once, after every hosted service is registered. The start order does not change.</summary>
    public static void Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var markerCount = 0;
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType != typeof(IHostedService) || services[index].IsKeyedService)
            {
                continue;
            }

            // Walking backwards, an insert never shifts an index still to visit.
            if (markerCount == 0)
            {
                services.Insert(index + 1, Marker());
                markerCount++;
            }

            services.Insert(index, Marker());
            markerCount++;
        }

        if (markerCount > 0)
        {
            services.AddSingleton(provider => new HostedServiceStartTiming(provider,
                provider.GetRequiredService<ILogger<HostedServiceStartTiming>>(),
                provider.GetRequiredService<TimeProvider>(),
                markerCount));
        }

        static ServiceDescriptor Marker() =>
            ServiceDescriptor.Singleton<IHostedService>(static provider => provider.GetRequiredService<HostedServiceStartTiming>());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The host starts hosted services one after another (ServicesStartConcurrently is off), so no call overlaps another.
        var now = _timeProvider.GetTimestamp();
        var call = _calls++;
        if (call == 0)
        {
            _firstTimestamp = now;
        }
        else
        {
            var elapsed = _timeProvider.GetElapsedTime(_previousTimestamp, now);
            if (elapsed >= SlowThreshold)
            {
                // Markers and services alternate, starting with a marker: the service before marker call k sits at 2k - 1.
                _hostedServices ??= _services.GetServices<IHostedService>().ToArray();
                _logger.LogInformation("Hosted service {HostedService} started in {ElapsedMs} ms.",
                    _hostedServices[(2 * call) - 1].GetType().Name,
                    Math.Round(elapsed.TotalMilliseconds));
            }
        }

        _previousTimestamp = now;
        if (call == _markerCount - 1)
        {
            _logger.LogInformation("Hosted services started in {TotalMs} ms ({Count} services)",
                Math.Round(_timeProvider.GetElapsedTime(_firstTimestamp, now).TotalMilliseconds),
                call);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
