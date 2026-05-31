namespace XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;

/// <summary>
///     Application service for container lifecycle behavior.
/// </summary>
public sealed class ContainerLifecycleService
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly IDockerRuntimeClient _dockerRuntimeClient;
    private readonly ManifestReconciler? _manifestReconciler;
    private readonly HostAgentRuntimeOptions _runtimeOptions;
    private readonly TimeProvider _timeProvider;

    public ContainerLifecycleService(IDockerRuntimeClient dockerRuntimeClient, TimeProvider timeProvider)
        : this(dockerRuntimeClient, timeProvider, null, new HostAgentRuntimeOptions())
    {
    }

    public ContainerLifecycleService(IDockerRuntimeClient dockerRuntimeClient,
        TimeProvider timeProvider,
        ManifestReconciler? manifestReconciler,
        HostAgentRuntimeOptions runtimeOptions)
    {
        _dockerRuntimeClient = dockerRuntimeClient;
        _timeProvider = timeProvider;
        _manifestReconciler = manifestReconciler;
        _runtimeOptions = runtimeOptions;
    }

    public async Task<IReadOnlyList<RuntimeComponentStatusDto>> ListContainersAsync(CancellationToken cancellationToken)
    {
        var containers = await _dockerRuntimeClient.ListContainersAsync(cancellationToken).ConfigureAwait(false);
        return containers.Select(ToRuntimeStatus).ToArray();
    }

    public Task<ContainerActionReportDto> StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("start",
            () => _dockerRuntimeClient.StartContainerAsync(containerName, cancellationToken),
            cancellationToken);
    }

    public Task<ContainerActionReportDto> StopContainerAsync(string containerName,
        TimeSpan? drainTimeout,
        CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("stop",
            () => _dockerRuntimeClient.StopContainerAsync(containerName, drainTimeout ?? DefaultDrainTimeout, cancellationToken),
            cancellationToken);
    }

    public Task<ContainerActionReportDto> RestartContainerAsync(string containerName,
        TimeSpan? drainTimeout,
        CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("restart",
            () => _dockerRuntimeClient.RestartContainerAsync(containerName, drainTimeout ?? DefaultDrainTimeout, cancellationToken),
            cancellationToken);
    }

    public async Task<ContainerActionReportDto> StopAllContainersAsync(TimeSpan? drainTimeout,
        CancellationToken cancellationToken)
    {
        var timeout = drainTimeout ?? ResolveDefaultDrainTimeout();
        var startedAt = _timeProvider.GetUtcNow();
        var diagnostics = new List<string>();
        var runtimeContainers = await ListRuntimeContainersAsync(cancellationToken).ConfigureAwait(false);

        var runningRuntimeContainers = runtimeContainers.Where(static container => container.IsRunning).ToArray();
        await Task.WhenAll(runningRuntimeContainers.Select(container =>
                      _dockerRuntimeClient.StopContainerAsync(container.Name, timeout, cancellationToken)))
                  .ConfigureAwait(false);

        var components = await ListContainersAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(components
                             .Where(IsRuntimeComponent)
                             .Where(static component => component.Health != ContainerHealth.Stopped)
                             .Select(static component => $"container-still-running:{component.Name}"));

        return new ContainerActionReportDto
        {
            Action = "stop-all",
            Succeeded = diagnostics.Count == 0,
            StartedAt = startedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Components = components,
            Diagnostics = diagnostics
        };
    }

    public async Task<ContainerActionReportDto> StartAllContainersAsync(CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var diagnostics = new List<string>();
        var manifest = _runtimeOptions.Manifest;

        if (manifest is null)
        {
            diagnostics.Add("static-manifest-missing");
            return new ContainerActionReportDto
            {
                Action = "start-all",
                Succeeded = false,
                StartedAt = startedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                Components = await ListContainersAsync(cancellationToken).ConfigureAwait(false),
                Diagnostics = diagnostics
            };
        }

        if (_manifestReconciler is null)
        {
            diagnostics.Add("manifest-reconciler-unavailable");
            return new ContainerActionReportDto
            {
                Action = "start-all",
                Succeeded = false,
                StartedAt = startedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                Components = await ListContainersAsync(cancellationToken).ConfigureAwait(false),
                Diagnostics = diagnostics
            };
        }

        await _dockerRuntimeClient.EnsureNetworkAsync(_runtimeOptions.RuntimeNetwork, cancellationToken).ConfigureAwait(false);

        var reconcileResult = await _manifestReconciler
                                    .ReconcileAsync(manifest, true, cancellationToken)
                                    .ConfigureAwait(false);
        diagnostics.AddRange(reconcileResult.Diagnostics);

        if (!reconcileResult.Succeeded)
        {
            return new ContainerActionReportDto
            {
                Action = "start-all",
                Succeeded = false,
                StartedAt = startedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                Components = reconcileResult.Components,
                Diagnostics = diagnostics
            };
        }

        foreach (var container in manifest.Containers.Where(ContainerBelongsToRuntimeNetwork))
        {
            await _dockerRuntimeClient.EnsureContainerAsync(container, cancellationToken).ConfigureAwait(false);
            await _dockerRuntimeClient.StartContainerAsync(container.Name, cancellationToken).ConfigureAwait(false);
        }

        var components = await ListContainersAsync(cancellationToken).ConfigureAwait(false);

        return new ContainerActionReportDto
        {
            Action = "start-all",
            Succeeded = diagnostics.Count == 0,
            StartedAt = startedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Components = components,
            Diagnostics = diagnostics
        };
    }

    private async Task<ContainerActionReportDto> ExecuteActionAsync(string action,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        await operation().ConfigureAwait(false);
        var components = await ListContainersAsync(cancellationToken).ConfigureAwait(false);

        return new ContainerActionReportDto
        {
            Action = action,
            Succeeded = true,
            StartedAt = startedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Components = components,
            Diagnostics = []
        };
    }

    private RuntimeComponentStatusDto ToRuntimeStatus(DockerContainerStatus container)
    {
        return new RuntimeComponentStatusDto
        {
            Name = container.Name,
            DesiredState = ContainerDesiredState.Running,
            Health = container.IsRunning ? ContainerHealth.Healthy : ContainerHealth.Stopped,
            ImageReference = container.ImageReference,
            DigestVerified = container.ImageReference.Contains("@sha256:", StringComparison.Ordinal),
            ObservedAt = _timeProvider.GetUtcNow(),
            Diagnostics = []
        };
    }

    private async Task<IReadOnlyList<DockerContainerStatus>> ListRuntimeContainersAsync(CancellationToken cancellationToken)
    {
        var containers = await _dockerRuntimeClient.ListContainersAsync(cancellationToken).ConfigureAwait(false);
        return containers.Where(container => container.NetworkNames.Contains(_runtimeOptions.RuntimeNetwork, StringComparer.Ordinal)).ToArray();
    }

    private bool IsRuntimeComponent(RuntimeComponentStatusDto component)
    {
        var manifest = _runtimeOptions.Manifest;
        if (manifest is not null)
        {
            return manifest.Containers.Any(container => string.Equals(container.Name, component.Name, StringComparison.Ordinal));
        }

        return true;
    }

    private bool ContainerBelongsToRuntimeNetwork(ContainerManifest container)
    {
        return string.Equals(container.Network, _runtimeOptions.RuntimeNetwork, StringComparison.Ordinal);
    }

    private TimeSpan ResolveDefaultDrainTimeout()
    {
        var manifestTimeoutSeconds = _runtimeOptions.Manifest?.RuntimeLimits.StopDrainTimeoutSeconds;
        return manifestTimeoutSeconds is > 0 ? TimeSpan.FromSeconds(manifestTimeoutSeconds.Value) : DefaultDrainTimeout;
    }
}
