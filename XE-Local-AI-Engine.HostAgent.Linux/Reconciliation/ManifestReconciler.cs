namespace XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;

using global::Docker.DotNet;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;

public sealed class ManifestReconciler
{
    private readonly Dictionary<ReconcileRequestKey, Task<ManifestReconcileResult>> _activeReconciles = [];
    private readonly Lock _coalesceGate = new();
    private readonly IDockerRuntimeClient _dockerRuntimeClient;
    private readonly TimeProvider _timeProvider;

    public ManifestReconciler(IDockerRuntimeClient dockerRuntimeClient, TimeProvider timeProvider)
    {
        _dockerRuntimeClient = dockerRuntimeClient;
        _timeProvider = timeProvider;
    }

    public async Task<ManifestReconcileResult> ReconcileAsync(HostAgentManifest manifest,
        bool pullImages = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Task<ManifestReconcileResult> reconcileTask;
        var ownsTask = false;
        var requestKey = new ReconcileRequestKey(manifest, pullImages);

        lock (_coalesceGate)
        {
            if (_activeReconciles.TryGetValue(requestKey, out var activeReconcile))
            {
                reconcileTask = activeReconcile;
            }
            else
            {
                reconcileTask = ReconcileCoreAsync(manifest, pullImages, cancellationToken);
                _activeReconciles.Add(requestKey, reconcileTask);
                ownsTask = true;
            }
        }

        try
        {
            return await reconcileTask.ConfigureAwait(false);
        }
        finally
        {
            if (ownsTask)
            {
                lock (_coalesceGate)
                {
                    _activeReconciles.Remove(requestKey);
                }
            }
        }
    }

    private async Task<ManifestReconcileResult> ReconcileCoreAsync(HostAgentManifest manifest,
        bool pullImages,
        CancellationToken cancellationToken)
    {
        var components = new List<RuntimeComponentStatusDto>(manifest.Containers.Count);
        var diagnostics = new List<string>();

        foreach (var container in manifest.Containers)
        {
            var component = await ReconcileContainerAsync(container, pullImages, diagnostics, cancellationToken).ConfigureAwait(false);
            components.Add(component);
        }

        return new ManifestReconcileResult
        {
            Succeeded = diagnostics.Count == 0,
            Components = components,
            Diagnostics = diagnostics
        };
    }

    private async Task<RuntimeComponentStatusDto> ReconcileContainerAsync(ContainerManifest container,
        bool pullImages,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var observedAt = _timeProvider.GetUtcNow();

        try
        {
            var image = DockerImageReference.Parse(container.Image);
            if (pullImages)
            {
                await _dockerRuntimeClient.PullImageAsync(image, cancellationToken).ConfigureAwait(false);
            }

            var observedDigest = await _dockerRuntimeClient.InspectImageDigestAsync(image, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(image.Digest, observedDigest, StringComparison.OrdinalIgnoreCase))
            {
                var diagnostic = $"{ReconcileDiagnosticCodes.ImageDigestMismatch}:{container.Name}";
                diagnostics.Add(diagnostic);

                return CreateComponentStatus(container,
                    ContainerHealth.Unhealthy,
                    false,
                    observedAt,
                    [diagnostic]);
            }

            return CreateComponentStatus(container,
                ContainerHealth.Healthy,
                true,
                observedAt,
                []);
        }
        catch (Exception exception) when (exception is FormatException or DockerApiException)
        {
            diagnostics.Add(exception.Message);
            return CreateComponentStatus(container,
                ContainerHealth.Unhealthy,
                false,
                observedAt,
                [exception.Message]);
        }
    }

    private static RuntimeComponentStatusDto CreateComponentStatus(ContainerManifest container,
        ContainerHealth health,
        bool digestVerified,
        DateTimeOffset observedAt,
        IReadOnlyList<string> diagnostics)
    {
        return new RuntimeComponentStatusDto
        {
            Name = container.Name,
            DesiredState = ContainerDesiredState.Running,
            Health = health,
            ImageReference = container.Image,
            DigestVerified = digestVerified,
            ObservedAt = observedAt,
            Diagnostics = diagnostics
        };
    }

    private sealed record ReconcileRequestKey(HostAgentManifest Manifest, bool PullImages);
}
