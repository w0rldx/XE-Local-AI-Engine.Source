namespace XE_Local_AI_Engine.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Services.Auth;
using XE_Local_AI_Engine.Services.Connection;

public sealed class HeartbeatBackgroundService : BackgroundService
{
    private readonly IWorkerHubConnection _hubConnection;
    private readonly ILogger<HeartbeatBackgroundService> _logger;
    private readonly IOptions<CentralPlatformOptions> _options;
    private readonly ITokenStore _tokenStore;

    public HeartbeatBackgroundService(IWorkerHubConnection hubConnection,
        ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> options,
        ILogger<HeartbeatBackgroundService> logger)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static TimeSpan TestDelayOverride { get; set; } = TimeSpan.Zero;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.Value.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = TestDelayOverride > TimeSpan.Zero ? TestDelayOverride : interval;
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (_hubConnection.State != WorkerConnectionState.Connected)
            {
                continue;
            }

            var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
            if (clientNodeId is null)
            {
                continue;
            }

            try
            {
                await _hubConnection.SendHeartbeatAsync(clientNodeId.Value, stoppingToken).ConfigureAwait(false);
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
}
