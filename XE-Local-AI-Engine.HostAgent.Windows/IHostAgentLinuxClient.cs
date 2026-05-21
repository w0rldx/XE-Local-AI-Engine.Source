namespace XE_Local_AI_Engine.HostAgent.Windows;

using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;

public interface IHostAgentLinuxClient
{
    Task<HostAgentStatusReply?> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ContainerActionReply?> StartAllContainersAsync(CancellationToken cancellationToken = default);

    Task<ContainerActionReply?> StopAllContainersAsync(TimeSpan drainTimeout, CancellationToken cancellationToken = default);
}
