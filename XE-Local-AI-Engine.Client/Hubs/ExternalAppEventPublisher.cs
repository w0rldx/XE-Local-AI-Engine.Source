namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Pushes a committed external-app change to that instance's group, superseding the no-op the application module
///     registers so a host without this hub stays resolvable.
/// </summary>
/// <remarks>
///     Both messages are content-free by contract: neither carries a variable, a value or anything else the engine
///     holds on the operator's behalf.
/// </remarks>
internal sealed class ExternalAppEventPublisher : IExternalAppEventPublisher
{
    private readonly IHubContext<ExternalAppHub> _hubContext;

    public ExternalAppEventPublisher(IHubContext<ExternalAppHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status,
        CancellationToken cancellationToken = default) =>
        _hubContext.Clients.Group(ExternalAppHubGroups.Instance(instanceId))
                  .SendAsync(ExternalAppHubEvents.Changed,
                      new ExternalAppChanged { InstanceId = instanceId, Sequence = sequence, Kind = ToWireKind(kind), Status = status.ToString() },
                      cancellationToken);

    public Task PublishPullProgressAsync(Guid instanceId,
        string service,
        int layerCount,
        int completedLayers,
        long bytes,
        CancellationToken cancellationToken = default) =>
        _hubContext.Clients.Group(ExternalAppHubGroups.Instance(instanceId))
                  .SendAsync(ExternalAppHubEvents.PullProgress,
                      new ExternalAppPullProgress { InstanceId = instanceId, Service = service, LayerCount = layerCount, CompletedLayers = completedLayers, Bytes = bytes },
                      cancellationToken);

    /// <summary>
    ///     The wire spelling of an event kind, written out rather than derived from the enum name.
    /// </summary>
    /// <remarks>
    ///     The subscriber switches on these literals: a capitalised name matches no arm and silently stops updating
    ///     the view, so renaming an enum member must not be able to change the wire contract by accident.
    /// </remarks>
    private static string ToWireKind(ExternalAppInstanceEventKind kind) =>
        kind switch
        {
            ExternalAppInstanceEventKind.Installed => "installed",
            ExternalAppInstanceEventKind.StartRequested => "startRequested",
            ExternalAppInstanceEventKind.Started => "started",
            ExternalAppInstanceEventKind.StopRequested => "stopRequested",
            ExternalAppInstanceEventKind.Stopped => "stopped",
            ExternalAppInstanceEventKind.Restarted => "restarted",
            ExternalAppInstanceEventKind.UpdateRequested => "updateRequested",
            ExternalAppInstanceEventKind.Updated => "updated",
            ExternalAppInstanceEventKind.ResetRequested => "resetRequested",
            ExternalAppInstanceEventKind.Reset => "reset",
            ExternalAppInstanceEventKind.UninstallRequested => "uninstallRequested",
            ExternalAppInstanceEventKind.Uninstalled => "uninstalled",
            ExternalAppInstanceEventKind.Failed => "failed",
            ExternalAppInstanceEventKind.RestoredOnBoot => "restoredOnBoot",
            ExternalAppInstanceEventKind.StoppedUnexpectedly => "stoppedUnexpectedly",
            ExternalAppInstanceEventKind.PermissionAccepted => "permissionAccepted",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown external app instance event kind.")
        };
}
