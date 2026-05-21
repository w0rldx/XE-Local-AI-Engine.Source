namespace XE_Local_AI_Engine.Client.Services.HostAgent;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public interface IHostAgentClient
{
    Task<HostAgentStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<HostCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RuntimeComponentStatusDto>> ListContainersAsync(CancellationToken cancellationToken);

    Task<ContainerActionReportDto> StartAllContainersAsync(CancellationToken cancellationToken);

    Task<ContainerActionReportDto> StopAllContainersAsync(TimeSpan drainTimeout, CancellationToken cancellationToken);

    Task<ContainerActionReportDto> StartContainerAsync(string containerName, CancellationToken cancellationToken);

    Task<ContainerActionReportDto> StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken);

    Task<ContainerActionReportDto> RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken);

    IAsyncEnumerable<HostAgentLogLineDto> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        CancellationToken cancellationToken);
}
