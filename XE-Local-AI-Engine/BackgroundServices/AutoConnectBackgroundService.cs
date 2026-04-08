namespace XE_Local_AI_Engine.BackgroundServices
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using XE_Local_AI_Engine.Configuration;
    using XE_Local_AI_Engine.Services.Auth;
    using XE_Local_AI_Engine.Services.Connection;

    public sealed class AutoConnectBackgroundService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
        public static TimeSpan TestStartupDelayOverride { get; set; } = TimeSpan.Zero;

        private readonly IWorkerHubConnection _hubConnection;
        private readonly ITokenStore _tokenStore;
        private readonly IOptions<CentralPlatformOptions> _options;
        private readonly ILogger<AutoConnectBackgroundService> _logger;
        private readonly CancellationTokenSource _shutdownSignal = new();

        public AutoConnectBackgroundService(
            IWorkerHubConnection hubConnection,
            ITokenStore tokenStore,
            IHostApplicationLifetime applicationLifetime,
            IOptions<CentralPlatformOptions> options,
            ILogger<AutoConnectBackgroundService> logger)
        {
            _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
            _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            ArgumentNullException.ThrowIfNull(applicationLifetime);
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            applicationLifetime.ApplicationStopping.Register(OnApplicationStopping);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
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

            if (_tokenStore.IsTokenExpired)
            {
                _logger.LogWarning("Worker token is expired. Auto-connect skipped until re-pairing completes.");
                return;
            }

            var reconnectDelays = _options.Value.ReconnectDelaysMs;
            var maxAttempts = _options.Value.MaxReconnectAttempts;

            for (var attempt = 0; attempt < maxAttempts && !linkedToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    _logger.LogInformation(
                        "Connecting to Central Platform automatically (attempt {Attempt}/{MaxAttempts}).",
                        attempt + 1,
                        maxAttempts);

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
                    var delayMilliseconds = GetRetryDelayMilliseconds(reconnectDelays, attempt);

                    _logger.LogWarning(
                        exception,
                        "Auto-connect attempt {Attempt} failed. Retrying in {DelayMilliseconds}ms.",
                        attempt + 1,
                        delayMilliseconds);

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), linkedToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }

            _logger.LogError(
                "Auto-connect failed after {MaxAttempts} attempts. Connect manually via the dashboard.",
                maxAttempts);
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
            _logger.LogInformation(
                "Application stopping. Auto-connect background service is cancelling pending startup/retry work.");
            _ = _shutdownSignal.CancelAsync();
        }

        private static int GetRetryDelayMilliseconds(IReadOnlyList<int> reconnectDelays, int attempt)
        {
            ArgumentNullException.ThrowIfNull(reconnectDelays);

            if (reconnectDelays.Count == 0)
            {
                return 1000;
            }

            return attempt < reconnectDelays.Count
                ? reconnectDelays[attempt]
                : reconnectDelays[^1];
        }
    }
}
