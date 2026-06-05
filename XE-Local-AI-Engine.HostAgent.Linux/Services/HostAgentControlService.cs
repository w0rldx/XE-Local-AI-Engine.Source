namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using global::Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Capabilities.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Logs;
using XE_Local_AI_Engine.HostAgent.Linux.Models;

public sealed class HostAgentControlService : HostAgentControl.HostAgentControlBase
{
    private readonly BootstrapModelReadinessService _bootstrapModelReadinessService;
    private readonly CapabilityDetector _capabilityDetector;
    private readonly ContainerLifecycleService _containerLifecycleService;
    private readonly ContainerLogService _containerLogService;

    public HostAgentControlService(ContainerLifecycleService containerLifecycleService,
        CapabilityDetector capabilityDetector,
        ContainerLogService containerLogService,
        BootstrapModelReadinessService bootstrapModelReadinessService)
    {
        _containerLifecycleService = containerLifecycleService;
        _capabilityDetector = capabilityDetector;
        _containerLogService = containerLogService;
        _bootstrapModelReadinessService = bootstrapModelReadinessService;
    }

    public override async Task<HostAgentStatusReply> GetStatus(Empty request, ServerCallContext context)
    {
        var readiness = _bootstrapModelReadinessService.GetSnapshot();
        var containers = await _containerLifecycleService.ListContainersAsync(context.CancellationToken).ConfigureAwait(false);
        var observedAt = Timestamp.FromDateTimeOffset(readiness.ObservedAt);

        var reply = new HostAgentStatusReply
        {
            State = HostAgentState.Running,
            DesiredState = HostAgentDesiredState.Running,
            RuntimeLifecycle = RuntimeLifecycle.Managed,
            BootstrapModelReady = readiness.IsReady,
            WebUiUrl = string.Empty,
            ObservedAt = observedAt,
            Diagnostics =
            {
                $"bootstrap-model:{readiness.ModelName}"
            }
        };

        reply.Components.AddRange(containers.Select(HostAgentGrpcMapper.ToRuntimeComponentStatusReply));
        reply.Diagnostics.AddRange(readiness.Diagnostics);
        return reply;
    }

    public override Task<HostCapabilitiesReply> GetCapabilities(Empty request, ServerCallContext context)
    {
        return GetCapabilitiesAsync(context.CancellationToken);
    }

    public override Task<ListContainersReply> ListContainers(Empty request, ServerCallContext context)
    {
        return ListContainersAsync(context.CancellationToken);
    }

    public override async Task<ContainerActionReply> StartContainer(ContainerActionRequest request, ServerCallContext context)
    {
        var report = await _containerLifecycleService.StartContainerAsync(request.ContainerName,
            context.CancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToContainerActionReply(report);
    }

    public override async Task<ContainerActionReply> StartAllContainers(AllContainersActionRequest request, ServerCallContext context)
    {
        var report = await _containerLifecycleService.StartAllContainersAsync(context.CancellationToken)
                                                     .ConfigureAwait(false);
        return HostAgentGrpcMapper.ToContainerActionReply(report);
    }

    public override async Task<ContainerActionReply> StopAllContainers(AllContainersActionRequest request, ServerCallContext context)
    {
        var report = await _containerLifecycleService.StopAllContainersAsync(GetDrainTimeout(request),
            context.CancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToContainerActionReply(report);
    }

    public override async Task<ContainerActionReply> StopContainer(ContainerActionRequest request, ServerCallContext context)
    {
        var report = await _containerLifecycleService.StopContainerAsync(request.ContainerName,
            GetDrainTimeout(request),
            context.CancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToContainerActionReply(report);
    }

    public override async Task<ContainerActionReply> RestartContainer(ContainerActionRequest request, ServerCallContext context)
    {
        var report = await _containerLifecycleService.RestartContainerAsync(request.ContainerName,
            GetDrainTimeout(request),
            context.CancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToContainerActionReply(report);
    }

    public override Task StreamLogs(StreamLogsRequest request,
        IServerStreamWriter<LogEntryReply> responseStream,
        ServerCallContext context)
    {
        return StreamLogsAsync(request, responseStream, context.CancellationToken);
    }

    private async Task<ListContainersReply> ListContainersAsync(CancellationToken cancellationToken)
    {
        var containers = await _containerLifecycleService.ListContainersAsync(cancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToListContainersReply(containers);
    }

    private async Task<HostCapabilitiesReply> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = await _capabilityDetector.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return HostAgentGrpcMapper.ToHostCapabilitiesReply(capabilities);
    }

    private static TimeSpan? GetDrainTimeout(ContainerActionRequest request)
    {
        return request.DrainTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.DrainTimeoutSeconds)
            : null;
    }

    private static TimeSpan? GetDrainTimeout(AllContainersActionRequest request)
    {
        return request.DrainTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.DrainTimeoutSeconds)
            : null;
    }

    private async Task StreamLogsAsync(StreamLogsRequest request,
        IServerStreamWriter<LogEntryReply> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var line in _containerLogService.StreamLogsAsync(request.ContainerName,
                           request.TailLines,
                           request.Follow,
                           cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(HostAgentGrpcMapper.ToLogEntryReply(line), cancellationToken)
                                .ConfigureAwait(false);
        }
    }
}
