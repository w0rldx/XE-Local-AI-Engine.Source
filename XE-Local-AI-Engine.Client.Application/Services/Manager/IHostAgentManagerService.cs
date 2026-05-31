namespace XE_Local_AI_Engine.Client.Services.Manager;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

/// <summary>
///     Application service for i host agent manager behavior.
/// </summary>
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

/// <summary>
///     Value object carrying host agent manager snapshot data.
/// </summary>
public sealed record HostAgentManagerSnapshot(
    HostAgentStatusDto Status,
    HostCapabilitiesDto Capabilities,
    IReadOnlyList<RuntimeComponentStatusDto> Components,
    ModelProviderHealth ModelProviderHealth,
    IReadOnlyList<LocalModelDescriptor> Models,
    HostAgentManifestView Manifest);

/// <summary>
///     Value object carrying host agent manifest view data.
/// </summary>
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

/// <summary>
///     Value object carrying host agent manifest container view data.
/// </summary>
public sealed record HostAgentManifestContainerView(
    string Name,
    string Image,
    string Network,
    IReadOnlyList<HostAgentManifestEnvironmentView> Environment,
    IReadOnlyList<HostAgentManifestVolumeView> Volumes);

/// <summary>
///     Value object carrying host agent manifest environment view data.
/// </summary>
public sealed record HostAgentManifestEnvironmentView(string Name, string Value);

/// <summary>
///     Value object carrying host agent manifest volume view data.
/// </summary>
public sealed record HostAgentManifestVolumeView(string Source, string Target, bool ReadOnly);
