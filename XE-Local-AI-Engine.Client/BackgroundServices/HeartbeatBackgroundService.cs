namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Application service for heartbeat background behavior.
/// </summary>
public sealed class HeartbeatBackgroundService : BackgroundService
{
    private static readonly TimeSpan DefaultCapabilityRefreshInterval = TimeSpan.FromMinutes(15);
    private readonly Lazy<ICapabilityReporter> _capabilityReporter;
    private readonly IWorkerHubConnection _hubConnection;
    private readonly ILogger<HeartbeatBackgroundService> _logger;
    private readonly IOptions<CentralPlatformOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITokenStore _tokenStore;
    private DateTimeOffset? _lastCapabilityRefreshAt;

    public HeartbeatBackgroundService(IWorkerHubConnection hubConnection,
        ITokenStore tokenStore,
        Lazy<ICapabilityReporter> capabilityReporter,
        IOptions<CentralPlatformOptions> options,
        ILogger<HeartbeatBackgroundService> logger,
        TimeProvider timeProvider)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public static TimeSpan TestDelayOverride { get; set; } = TimeSpan.Zero;

    public static TimeSpan TestCapabilityRefreshIntervalOverride { get; set; } = TimeSpan.Zero;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.Value.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = TestDelayOverride > TimeSpan.Zero ? TestDelayOverride : interval;
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (_hubConnection.State != WorkerConnectionState.Connected)
            {
                continue;
            }

            var clientNodeId = await _tokenStore.GetClientNodeIdAsync();
            if (clientNodeId is null)
            {
                continue;
            }

            try
            {
                await _hubConnection.SendHeartbeatAsync(clientNodeId.Value, stoppingToken);
                await RefreshCapabilitiesIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to send heartbeat.");
            }
        }
    }

    private async Task RefreshCapabilitiesIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var refreshInterval = TestCapabilityRefreshIntervalOverride > TimeSpan.Zero
            ? TestCapabilityRefreshIntervalOverride
            : DefaultCapabilityRefreshInterval;

        if (_lastCapabilityRefreshAt is not null && now - _lastCapabilityRefreshAt.Value < refreshInterval)
        {
            return;
        }

        _lastCapabilityRefreshAt = now;
        try
        {
            await _capabilityReporter.Value.ReportToApiAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to refresh worker capabilities after heartbeat.");
        }
    }
}
