namespace XE_Local_AI_Engine.HostAgent.Windows;

/// <summary>
///     Application service for admin token initialization hosted behavior.
/// </summary>
public sealed class AdminTokenInitializationHostedService : IHostedService
{
    private readonly ILogger<AdminTokenInitializationHostedService> _logger;
    private readonly HostAgentSecretStore _secretStore;

    public AdminTokenInitializationHostedService(HostAgentSecretStore secretStore,
        ILogger<AdminTokenInitializationHostedService> logger)
    {
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("HostAgent.Windows secret storage is disabled because the process is not running on Windows.");
            return;
        }

        await _secretStore.GetOrCreateAdminTokenAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("HostAgent.Windows admin token secret storage initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
