namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

using Microsoft.Extensions.Options;

/// <summary>
///     Application service for wsl supervisor hosted behavior.
/// </summary>
public sealed class WslSupervisorHostedService : BackgroundService
{
    private readonly DesiredStateStore _desiredStateStore;
    private readonly Wsl2Driver _driver;
    private readonly ILogger<WslSupervisorHostedService> _logger;
    private readonly HostAgentWslOptions _options;
    private int _consecutiveStatusFailures;

    public WslSupervisorHostedService(Wsl2Driver driver,
        DesiredStateStore desiredStateStore,
        IOptions<HostAgentWslOptions> options,
        ILogger<WslSupervisorHostedService> logger)
    {
        _driver = driver;
        _desiredStateStore = desiredStateStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("WSL supervisor disabled because HostAgent.Windows is not running on Windows.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSupervisorCycleAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(_options.SupervisorInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task RunSupervisorCycleAsync(CancellationToken cancellationToken = default)
    {
        var desiredState = await _desiredStateStore.GetDesiredStateAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(desiredState, DesiredStateStore.Stopped, StringComparison.Ordinal))
        {
            _consecutiveStatusFailures = 0;
            return;
        }

        var runningDistros = await _driver.ListRunningDistributionsAsync(cancellationToken).ConfigureAwait(false);
        if (!runningDistros.Contains(_options.DistroName, StringComparer.Ordinal))
        {
            _logger.LogWarning("Managed WSL distro is not running. Cold-starting HostAgent.Linux user unit.");
            await _driver.ColdStartAsync(cancellationToken).ConfigureAwait(false);
            _consecutiveStatusFailures = 0;
            return;
        }

        var status = await _driver.ReadHostAgentStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Succeeded)
        {
            _consecutiveStatusFailures = 0;
            return;
        }

        _consecutiveStatusFailures++;
        _logger.LogWarning("HostAgent.Linux status probe failed {FailureCount} consecutive time(s).", _consecutiveStatusFailures);

        if (_consecutiveStatusFailures >= 3)
        {
            _logger.LogWarning("HostAgent.Linux failed three consecutive probes. Terminating WSL distro and cold-starting.");
            await _driver.TerminateAsync(cancellationToken).ConfigureAwait(false);
            await _driver.ColdStartAsync(cancellationToken).ConfigureAwait(false);
            _consecutiveStatusFailures = 0;
        }
    }
}
