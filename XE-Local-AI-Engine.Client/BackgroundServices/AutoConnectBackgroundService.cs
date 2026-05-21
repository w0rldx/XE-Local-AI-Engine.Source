namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.HostAgent;

public sealed class AutoConnectBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private readonly ConnectionState _connectionState;
    private readonly IHostAgentReadinessClient _hostAgentReadinessClient;

    private readonly IWorkerHubConnection _hubConnection;
    private readonly ILogger<AutoConnectBackgroundService> _logger;
    private readonly IOptions<CentralPlatformOptions> _options;
    private readonly CancellationTokenSource _shutdownSignal = new();
    private readonly ITokenStore _tokenStore;
    private readonly IWorkerEventDispatcher _workerEventDispatcher;

    public AutoConnectBackgroundService(IWorkerHubConnection hubConnection,
        ITokenStore tokenStore,
        IHostAgentReadinessClient hostAgentReadinessClient,
        ConnectionState connectionState,
        IWorkerEventDispatcher workerEventDispatcher,
        IHostApplicationLifetime applicationLifetime,
        IOptions<CentralPlatformOptions> options,
        ILogger<AutoConnectBackgroundService> logger)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _hostAgentReadinessClient = hostAgentReadinessClient ?? throw new ArgumentNullException(nameof(hostAgentReadinessClient));
        _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
        _workerEventDispatcher = workerEventDispatcher ?? throw new ArgumentNullException(nameof(workerEventDispatcher));
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        applicationLifetime.ApplicationStopping.Register(OnApplicationStopping);
    }

    public static TimeSpan TestStartupDelayOverride { get; set; } = TimeSpan.Zero;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,
            _shutdownSignal.Token);

        var linkedToken = linkedCancellationTokenSource.Token;

        try
        {
            var startupDelay = TestStartupDelayOverride > TimeSpan.Zero ? TestStartupDelayOverride : StartupDelay;
            await Task.Delay(startupDelay, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
        {
            return;
        }

        if (!_tokenStore.IsPaired)
        {
            _logger.LogInformation("Worker is not paired. Waiting for manual pairing via UI.");
            return;
        }

        if (!_tokenStore.AutoConnectOnStart)
        {
            _logger.LogInformation("Auto-connect disabled by local setting.");
            return;
        }

        var options = _options.Value;
        var retryPolicy = new WorkerReconnectPolicy(options);

        if (options.ReconnectDelaysMs.Count > 0)
        {
            _logger.LogWarning("CentralPlatform:ReconnectDelaysMs is deprecated and ignored. Use ReconnectBackoffBaseMs/ReconnectBackoffMaxMs/ReconnectBackoffJitterMs/ReconnectMaxAttempts instead.");
        }

        var attempt = 0;
        while (!linkedToken.IsCancellationRequested)
        {
            try
            {
                if (!await WaitForBootstrapModelAsync(retryPolicy, linkedToken).ConfigureAwait(false))
                {
                    return;
                }

                _logger.LogInformation("Connecting to Central Platform automatically (attempt {Attempt}{AttemptSuffix}).",
                    attempt + 1,
                    options.ReconnectMaxAttempts == 0 ? string.Empty : $"/{options.ReconnectMaxAttempts}");

                await _hubConnection.ConnectAsync(linkedToken).ConfigureAwait(false);

                _logger.LogInformation("Connected to Central Platform successfully.");
                return;
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var delay = retryPolicy.GetDelay(attempt);
                if (delay is null)
                {
                    break;
                }

                _logger.LogWarning(exception,
                    "Auto-connect attempt {Attempt} failed. Retrying in {DelayMilliseconds}ms.",
                    attempt + 1,
                    delay.Value.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay.Value, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    return;
                }

                attempt++;
            }
        }

        _logger.LogError("Auto-connect failed after exhausting the configured initial-connect retry policy. Connect manually via the dashboard.");
    }

    private async Task<bool> WaitForBootstrapModelAsync(WorkerReconnectPolicy retryPolicy, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await _hostAgentReadinessClient.IsBootstrapModelReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _connectionState.TransitionTo(WorkerConnectionState.PreparingModel, "Preparing bootstrap model before platform connection.");
            var delay = retryPolicy.GetDelay(attempt);
            if (delay is null)
            {
                _logger.LogError("Bootstrap model did not become ready before exhausting the configured startup-gate retry policy.");
                return false;
            }

            _logger.LogInformation("Bootstrap model is not ready. WorkerHub connection remains gated; retrying in {DelayMilliseconds}ms.",
                delay.Value.TotalMilliseconds);

            await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            attempt++;
        }

        return false;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdownSignal.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _shutdownSignal.Dispose();
        base.Dispose();
    }

    private void OnApplicationStopping()
    {
        _workerEventDispatcher.StopAcceptingRemoteInvocations();
        _logger.LogInformation("Application stopping. Auto-connect background service is cancelling pending startup/retry work.");
        _ = _shutdownSignal.CancelAsync();
    }
}
