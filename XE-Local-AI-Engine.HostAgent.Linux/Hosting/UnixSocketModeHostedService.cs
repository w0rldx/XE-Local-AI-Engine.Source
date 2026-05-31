namespace XE_Local_AI_Engine.HostAgent.Linux.Hosting;

/// <summary>
///     Application service for unix socket mode hosted behavior.
/// </summary>
public sealed class UnixSocketModeHostedService : IHostedService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<UnixSocketModeHostedService> _logger;
    private readonly HostAgentSocketOptions _socketOptions;

    public UnixSocketModeHostedService(IHostApplicationLifetime applicationLifetime,
        HostAgentSocketOptions socketOptions,
        ILogger<UnixSocketModeHostedService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _socketOptions = socketOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _applicationLifetime.ApplicationStarted.Register(() => _ = SetSocketModeAsync(_applicationLifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task SetSocketModeAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (File.Exists(_socketOptions.SocketPath))
            {
                File.SetUnixFileMode(_socketOptions.SocketPath, _socketOptions.SocketFileMode);
                _logger.LogInformation("HostAgent gRPC socket mode set to {Mode} for {SocketPath}.",
                    _socketOptions.SocketFileMode,
                    _socketOptions.SocketPath);
                return;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("HostAgent gRPC socket file was not found for mode update: {SocketPath}.",
            _socketOptions.SocketPath);
    }
}
