namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Pushes a committed external-app change to that instance's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable. Both messages are content-free by contract: neither
///     carries a variable, a value or anything else the engine holds on the operator's behalf.
/// </summary>
internal sealed class ExternalAppEventPublisher(IHubContext<ExternalAppHub> hubContext) : IExternalAppEventPublisher
{
    public Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status,
        CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(ExternalAppHubGroups.Instance(instanceId))
                  .SendAsync(ExternalAppHubEvents.Changed,
                      new ExternalAppChanged(instanceId, sequence, ToWireKind(kind), status.ToString()),
                      cancellationToken);

    public Task PublishPullProgressAsync(Guid instanceId,
        string service,
        int layerCount,
        int completedLayers,
        long bytes,
        CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(ExternalAppHubGroups.Instance(instanceId))
                  .SendAsync(ExternalAppHubEvents.PullProgress,
                      new ExternalAppPullProgress(instanceId, service, layerCount, completedLayers, bytes),
                      cancellationToken);

    /// <summary>
    ///     The wire spelling of an event kind, written out rather than derived from the enum name. The subscriber
    ///     switches on these literals: a capitalised name matches no arm and silently stops updating the view, and
    ///     renaming an enum member must not be able to change the wire contract by accident.
    /// </summary>
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
