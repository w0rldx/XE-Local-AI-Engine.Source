namespace XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

internal static class RuntimeManagerMapper
{
    public static RuntimeContainerActionResponse ToResponse(this ContainerActionReportDto report, string containerName)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new RuntimeContainerActionResponse
        {
            ContainerName = containerName,
            Action = report.Action,
            Succeeded = report.Succeeded,
            StartedAt = report.StartedAt,
            CompletedAt = report.CompletedAt,
            Components = report.Components,
            Diagnostics = report.Diagnostics
        };
    }

    public static RuntimeManagerStatusResponse ToResponse(this HostAgentManagerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new RuntimeManagerStatusResponse
        {
            Status = snapshot.Status,
            Capabilities = snapshot.Capabilities,
            Components = snapshot.Components,
            ModelProviderHealth = snapshot.ModelProviderHealth.ToResponse(),
            Models = snapshot.Models.Select(ToResponse).ToArray(),
            Manifest = snapshot.Manifest.ToResponse()
        };
    }

    private static RuntimeModelProviderHealthResponse ToResponse(this ModelProviderHealth health)
    {
        return new RuntimeModelProviderHealthResponse
        {
            ProviderName = health.ProviderName,
            IsHealthy = health.IsHealthy,
            ObservedAt = health.ObservedAt,
            Diagnostics = health.Diagnostics
        };
    }

    private static RuntimeLocalModelResponse ToResponse(LocalModelDescriptor model)
    {
        return new RuntimeLocalModelResponse
        {
            ModelName = model.ModelName,
            ProviderName = model.ProviderName,
            IsAvailable = model.IsAvailable,
            SizeBytes = model.SizeBytes,
            ModifiedAt = model.ModifiedAt,
            MaxContextTokens = model.MaxContextTokens
        };
    }

    private static RuntimeManifestResponse ToResponse(this HostAgentManifestView manifest)
    {
        return new RuntimeManifestResponse
        {
            Available = manifest.Available,
            SchemaVersion = manifest.SchemaVersion,
            RuntimeMode = manifest.RuntimeMode,
            BootstrapModel = manifest.BootstrapModel,
            DefaultChatModel = manifest.DefaultChatModel,
            MaxRuntimeDiskGb = manifest.MaxRuntimeDiskGb,
            StopDrainTimeoutSeconds = manifest.StopDrainTimeoutSeconds,
            Containers = manifest.Containers.Select(ToResponse).ToArray(),
            Diagnostics = manifest.Diagnostics
        };
    }

    private static RuntimeManifestContainerResponse ToResponse(HostAgentManifestContainerView container)
    {
        return new RuntimeManifestContainerResponse
        {
            Name = container.Name,
            Image = container.Image,
            Network = container.Network,
            Environment = container.Environment.Select(ToResponse).ToArray(),
            Volumes = container.Volumes.Select(ToResponse).ToArray()
        };
    }

    private static RuntimeManifestEnvironmentResponse ToResponse(HostAgentManifestEnvironmentView environment)
    {
        return new RuntimeManifestEnvironmentResponse
        {
            Name = environment.Name,
            Value = environment.Value
        };
    }

    private static RuntimeManifestVolumeResponse ToResponse(HostAgentManifestVolumeView volume)
    {
        return new RuntimeManifestVolumeResponse
        {
            Source = volume.Source,
            Target = volume.Target,
            ReadOnly = volume.ReadOnly
        };
    }
}
