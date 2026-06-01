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
        return ScopeToOwnedComponents(containers).Select(ToRuntimeStatus).ToArray();
    }

    public Task<ContainerActionReportDto> StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("start",
            containerName,
            () => _dockerRuntimeClient.StartContainerAsync(containerName, cancellationToken),
            cancellationToken);
    }

    public Task<ContainerActionReportDto> StopContainerAsync(string containerName,
        TimeSpan? drainTimeout,
        CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("stop",
            containerName,
            () => _dockerRuntimeClient.StopContainerAsync(containerName, drainTimeout ?? DefaultDrainTimeout, cancellationToken),
            cancellationToken);
    }

    public Task<ContainerActionReportDto> RestartContainerAsync(string containerName,
        TimeSpan? drainTimeout,
        CancellationToken cancellationToken)
    {
        return ExecuteActionAsync("restart",
            containerName,
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
        string containerName,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();

        if (!ContainerOwnership.Owns(_runtimeOptions.Manifest, containerName))
        {
            return new ContainerActionReportDto
            {
                Action = action,
                Succeeded = false,
                StartedAt = startedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                Components = await ListContainersAsync(cancellationToken).ConfigureAwait(false),
                Diagnostics = [$"container-not-owned:{containerName}"]
            };
        }

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

    /// <summary>
    ///     Restricts a raw daemon container listing to the runtime components this node manages and collapses
    ///     terminated duplicates. Ownership is scoped by the node's static manifest (the containers it declares).
    ///     The stance is FAIL-CLOSED: without a manifest the node owns nothing, so the listing is empty rather than
    ///     exposing every daemon container (incl. cross-app containers) when no ownership boundary is known.
    ///     Production nodes always boot with a manifest, so their scoping is unchanged. Among containers sharing a
    ///     name (e.g. a stale, terminated duplicate alongside the live one) a single representative is kept,
    ///     preferring a running instance so stopped stale dupes do not surface in the Manager UI.
    /// </summary>
    private IReadOnlyList<DockerContainerStatus> ScopeToOwnedComponents(IReadOnlyList<DockerContainerStatus> containers)
    {
        var owned = containers.Where(IsOwnedComponent);

        return owned
               .GroupBy(static container => container.Name, StringComparer.Ordinal)
               .Select(SelectRepresentative)
               .ToArray();
    }

    private bool IsOwnedComponent(DockerContainerStatus container)
    {
        return ContainerOwnership.Owns(_runtimeOptions.Manifest, container.Name);
    }

    private static DockerContainerStatus SelectRepresentative(IGrouping<string, DockerContainerStatus> group)
    {
        return group.FirstOrDefault(static container => container.IsRunning) ?? group.First();
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
        return ContainerOwnership.Owns(_runtimeOptions.Manifest, component.Name);
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
