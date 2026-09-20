namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Startup <see cref="IHostedService" /> that removes Development Mode containers orphaned by a previous run of THIS installation, the
///     container-provider counterpart to <c>SandboxOrphanReaper</c>.
/// </summary>
/// <remarks>
///     Separate from that reaper because the two share no mechanism — one reads on-disk markers and signals process groups, the other
///     queries a daemon by label — and merging them would give the process provider's sweep a Docker dependency it must not have. Three
///     gates precede any removal: Development Mode must actually RESOLVE to the container provider, so a node that never opted in does not
///     touch the daemon even to list; the preflight must be <see cref="DockerDaemonPreflight.Ready" />, settling reachability, permission
///     and the identity pin; and the daemon-side label filter must match this installation. The whole body is guarded.
/// </remarks>
internal sealed class DockerSandboxOrphanSweeper : IHostedService
{
    private readonly ILogger<DockerSandboxOrphanSweeper> _logger;
    private readonly IDockerDaemonPreflightService _preflight;
    private readonly IServiceProvider _services;

    public DockerSandboxOrphanSweeper(IServiceProvider services,
        IDockerDaemonPreflightService preflight,
        ILogger<DockerSandboxOrphanSweeper> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Startup Development Mode container sweep failed; continuing startup.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // Resolved through the selector rather than off the configuration key, so "unset means follow the agent role" is decided in one
        // place. A node on the process or fake provider returns something else, and this returns without a single daemon call.
        if (SandboxProviderSelector.ResolveDevelopment(_services) is not DockerSandboxRuntimeProvider provider)
        {
            return;
        }

        var preflight = await _preflight.InspectAsync(cancellationToken);
        if (!preflight.Ready)
        {
            _logger.LogInformation("Skipping the Development Mode container sweep: the daemon preflight reports {Status}.", preflight.Status);
            return;
        }

        var removed = await provider.SweepOrphanedContainersAsync(cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("Removed {Removed} orphaned Development Mode container(s) left by a previous run.", removed);
        }
    }
}
