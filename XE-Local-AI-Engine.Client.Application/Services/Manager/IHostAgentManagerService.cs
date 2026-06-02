namespace XE_Local_AI_Engine.Client.Services.Manager;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public interface IHostAgentManagerService
{
    Task<HostAgentManagerSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken);

    Task<ContainerActionReportDto> ExecuteContainerActionAsync(string containerName,
        HostAgentContainerAction action,
        TimeSpan drainTimeout,
        CancellationToken cancellationToken);

    IAsyncEnumerable<HostAgentLogLineDto> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        CancellationToken cancellationToken);
}

/// <summary>
///     Enumerates supported host agent container action values.
/// </summary>
public enum HostAgentContainerAction
{
    Start,
    Stop,
    Restart
}

public sealed record HostAgentManagerSnapshot(
    HostAgentStatusDto Status,
    HostCapabilitiesDto Capabilities,
    IReadOnlyList<RuntimeComponentStatusDto> Components,
    ModelProviderHealth ModelProviderHealth,
    IReadOnlyList<LocalModelDescriptor> Models,
    HostAgentManifestView Manifest);

public sealed record HostAgentManifestView(
    bool Available,
    int? SchemaVersion,
    string RuntimeMode,
    string BootstrapModel,
    string DefaultChatModel,
    int? MaxRuntimeDiskGb,
    int? StopDrainTimeoutSeconds,
    IReadOnlyList<HostAgentManifestContainerView> Containers,
    IReadOnlyList<string> Diagnostics);

public sealed record HostAgentManifestContainerView(
    string Name,
    string Image,
    string Network,
    IReadOnlyList<HostAgentManifestEnvironmentView> Environment,
    IReadOnlyList<HostAgentManifestVolumeView> Volumes);

public sealed record HostAgentManifestEnvironmentView(string Name, string Value);

public sealed record HostAgentManifestVolumeView(string Source, string Target, bool ReadOnly);
