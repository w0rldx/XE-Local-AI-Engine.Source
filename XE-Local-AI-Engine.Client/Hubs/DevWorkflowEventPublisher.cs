namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Pushes a committed development-workflow change to that run's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
internal sealed class DevWorkflowEventPublisher(IHubContext<DevWorkflowRunHub> hubContext) : IDevWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, DevWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(DevWorkflowHubGroups.Run(runId))
                  .SendAsync(DevWorkflowHubEvents.Changed, new DevWorkflowChanged(runId, sequence, ToWireKind(kind)), cancellationToken);

    /// <summary>
    ///     The wire spelling of a change kind, written out rather than derived from the enum name. The subscriber
    ///     switches on these literals: a capitalised name matches no arm and silently stops updating the view, and
    ///     renaming an enum member must not be able to change the wire contract by accident.
    /// </summary>
    private static string ToWireKind(DevWorkflowChangeKind kind) =>
        kind switch
        {
            DevWorkflowChangeKind.Run => "run",
            DevWorkflowChangeKind.Node => "node",
            DevWorkflowChangeKind.Gate => "gate",
            DevWorkflowChangeKind.Artifact => "artifact",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown development workflow change kind.")
        };
}
