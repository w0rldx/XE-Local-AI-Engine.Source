namespace XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;

using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public sealed class RuntimeManagerStatusResponse
{
    public required HostAgentStatusDto Status { get; init; }

    public required HostCapabilitiesDto Capabilities { get; init; }

    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    public required RuntimeModelProviderHealthResponse ModelProviderHealth { get; init; }

    public required IReadOnlyList<RuntimeLocalModelResponse> Models { get; init; }

    public required RuntimeManifestResponse Manifest { get; init; }
}

public sealed class RuntimeContainerActionRequest
{
    public string? ContainerName { get; init; }

    public string? Action { get; init; }

    public int? DrainTimeoutSeconds { get; init; }
}

public sealed class RuntimeContainerActionResponse
{
    public required string ContainerName { get; init; }

    public required string Action { get; init; }

    public required bool Succeeded { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public sealed class RuntimeLogsRequest
{
    public string? ContainerName { get; init; }

    public int? TailLines { get; init; }

    public bool Follow { get; init; } = true;
}

public sealed class RuntimeLogLineResponse
{
    public required string ContainerName { get; init; }

    public required string Stream { get; init; }

    public required string Line { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed class RuntimeModelProviderHealthResponse
{
    public required string ProviderName { get; init; }

    public required bool IsHealthy { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public sealed class RuntimeLocalModelResponse
{
    public required string ModelName { get; init; }

    public required string ProviderName { get; init; }

    public required bool IsAvailable { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public int? MaxContextTokens { get; init; }
}

public sealed class RuntimeManifestResponse
{
    public required bool Available { get; init; }

    public int? SchemaVersion { get; init; }

    public required string RuntimeMode { get; init; }

    public required string BootstrapModel { get; init; }

    public required string DefaultChatModel { get; init; }

    public int? MaxRuntimeDiskGb { get; init; }

    public int? StopDrainTimeoutSeconds { get; init; }

    public required IReadOnlyList<RuntimeManifestContainerResponse> Containers { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public sealed class RuntimeManifestContainerResponse
{
    public required string Name { get; init; }

    public required string Image { get; init; }

    public required string Network { get; init; }

    public required IReadOnlyList<RuntimeManifestEnvironmentResponse> Environment { get; init; }

    public required IReadOnlyList<RuntimeManifestVolumeResponse> Volumes { get; init; }
}

public sealed class RuntimeManifestEnvironmentResponse
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

public sealed class RuntimeManifestVolumeResponse
{
    public required string Source { get; init; }

    public required string Target { get; init; }

    public required bool ReadOnly { get; init; }
}

internal static class RuntimeManagerEndpointDtoMapper
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

    public static RuntimeLogLineResponse ToResponse(this HostAgentLogLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new RuntimeLogLineResponse
        {
            ContainerName = line.ContainerName,
            Stream = line.Stream,
            Line = line.Line,
            ObservedAt = line.ObservedAt
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
