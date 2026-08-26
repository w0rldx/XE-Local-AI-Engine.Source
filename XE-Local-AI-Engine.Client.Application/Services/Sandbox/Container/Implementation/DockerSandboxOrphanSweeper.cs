namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Startup <see cref="IHostedService" /> that removes Development Mode containers orphaned by a previous run of
///     THIS installation. The container-provider counterpart to <c>SandboxOrphanReaper</c>, which covers the process
///     provider's jails and knows nothing about containers.
///     <para>
///         Separate from that reaper rather than folded into it, because the two share no mechanism: one reads on-disk
///         markers and signals process groups, the other queries a daemon by label. Merging them would give the
///         process provider's sweep a Docker dependency it must not have — provider selection is per feature, and
///         AgentHome must not acquire a container runtime requirement by association (ADR 0004).
///     </para>
///     <para>
///         <b>Three gates before anything is removed</b>, in this order and for different reasons:
///     </para>
///     <list type="number">
///         <item>
///             Development Mode must actually resolve to the container provider. The Docker types are registered
///             unconditionally, so on a node that never opted in (<c>Development:Sandbox:Provider</c> unset, the
///             shipped default) this must not touch the daemon at all — not even to list.
///         </item>
///         <item>
///             The daemon preflight must be <see cref="DockerDaemonPreflight.Ready" />. It settles reachability,
///             permission and the daemon-identity pin in one call, so a sweep never runs against a daemon this node
///             has not approved — which would mean removing containers on a machine the operator never pointed us at.
///         </item>
///         <item>
///             The daemon-side label filter must match this installation, which
///             <c>DockerSandboxRuntimeProvider.SweepOrphanedContainersAsync</c> applies.
///         </item>
///     </list>
///     <para>
///         The whole body is guarded: like the process reaper, a sweep failure must never block application start.
///     </para>
/// </summary>
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
            await SweepAsync(cancellationToken).ConfigureAwait(false);
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
        // Resolved through the selector rather than off the configuration key, so "unset means follow the agent role"
        // is decided in exactly one place. A node on the process or fake provider returns something that is not the
        // container provider, and this returns without a single daemon call.
        if (SandboxProviderSelector.ResolveDevelopment(_services) is not DockerSandboxRuntimeProvider provider)
        {
            return;
        }

        var preflight = await _preflight.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!preflight.Ready)
        {
            _logger.LogInformation("Skipping the Development Mode container sweep: the daemon preflight reports {Status}.", preflight.Status);
            return;
        }

        var removed = await provider.SweepOrphanedContainersAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            _logger.LogInformation("Removed {Removed} orphaned Development Mode container(s) left by a previous run.", removed);
        }
    }
}
