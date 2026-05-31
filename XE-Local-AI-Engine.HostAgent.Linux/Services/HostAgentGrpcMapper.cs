namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using Contracts = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using GrpcContracts = XE_Local_AI_Engine.HostAgent.Grpc.Contracts;

/// <summary>
///     Maps host agent grpc mapper values between domain and transport shapes.
/// </summary>
public static class HostAgentGrpcMapper
{
    public static GrpcContracts.ListContainersReply ToListContainersReply(IReadOnlyList<Contracts.RuntimeComponentStatusDto> containers)
    {
        var reply = new GrpcContracts.ListContainersReply();
        reply.Containers.AddRange(containers.Select(ToRuntimeComponentStatusReply));
        return reply;
    }

    public static GrpcContracts.ContainerActionReply ToContainerActionReply(Contracts.ContainerActionReportDto report)
    {
        var reply = new GrpcContracts.ContainerActionReply
        {
            Action = report.Action,
            Succeeded = report.Succeeded,
            StartedAt = Timestamp.FromDateTimeOffset(report.StartedAt),
            CompletedAt = Timestamp.FromDateTimeOffset(report.CompletedAt),
            Diagnostics =
            {
                report.Diagnostics
            }
        };
        reply.Components.AddRange(report.Components.Select(ToRuntimeComponentStatusReply));
        return reply;
    }

    public static GrpcContracts.HostCapabilitiesReply ToHostCapabilitiesReply(Contracts.HostCapabilitiesDto capabilities)
    {
        return new GrpcContracts.HostCapabilitiesReply
        {
            CpuAvailable = capabilities.CpuAvailable,
            NvidiaGpuInference = capabilities.NvidiaGpuInference,
            GpuRuntimeConfigured = capabilities.GpuRuntimeConfigured,
            AmdGpuStatus = capabilities.AmdGpuStatus,
            RuntimeDiskBytes = capabilities.RuntimeDiskBytes,
            ObservedAt = Timestamp.FromDateTimeOffset(capabilities.ObservedAt),
            Diagnostics =
            {
                capabilities.Diagnostics
            }
        };
    }

    public static GrpcContracts.LogEntryReply ToLogEntryReply(DockerLogLine logLine)
    {
        return new GrpcContracts.LogEntryReply
        {
            ContainerName = logLine.ContainerName,
            Stream = logLine.Stream,
            Line = logLine.Line,
            ObservedAt = Timestamp.FromDateTimeOffset(logLine.ObservedAt)
        };
    }

    public static GrpcContracts.RuntimeComponentStatusReply ToRuntimeComponentStatusReply(Contracts.RuntimeComponentStatusDto component)
    {
        return new GrpcContracts.RuntimeComponentStatusReply
        {
            Name = component.Name,
            DesiredState = ToGrpcDesiredState(component.DesiredState),
            Health = ToGrpcHealth(component.Health),
            ImageReference = component.ImageReference,
            DigestVerified = component.DigestVerified,
            ObservedAt = Timestamp.FromDateTimeOffset(component.ObservedAt),
            Diagnostics =
            {
                component.Diagnostics
            }
        };
    }

    private static GrpcContracts.ContainerDesiredState ToGrpcDesiredState(Contracts.ContainerDesiredState desiredState)
    {
        return desiredState switch
        {
            Contracts.ContainerDesiredState.Running => GrpcContracts.ContainerDesiredState.Running,
            Contracts.ContainerDesiredState.Stopped => GrpcContracts.ContainerDesiredState.Stopped,
            _ => GrpcContracts.ContainerDesiredState.Unspecified
        };
    }

    private static GrpcContracts.ContainerHealth ToGrpcHealth(Contracts.ContainerHealth health)
    {
        return health switch
        {
            Contracts.ContainerHealth.Starting => GrpcContracts.ContainerHealth.Starting,
            Contracts.ContainerHealth.Healthy => GrpcContracts.ContainerHealth.Healthy,
            Contracts.ContainerHealth.Unhealthy => GrpcContracts.ContainerHealth.Unhealthy,
            Contracts.ContainerHealth.Stopped => GrpcContracts.ContainerHealth.Stopped,
            _ => GrpcContracts.ContainerHealth.Unspecified
        };
    }
}
