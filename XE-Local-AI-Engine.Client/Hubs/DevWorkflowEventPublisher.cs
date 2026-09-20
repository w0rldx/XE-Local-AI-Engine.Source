namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Pushes a committed development-workflow change to that run's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
internal sealed class DevWorkflowEventPublisher : IDevWorkflowEventPublisher
{
    private readonly IHubContext<DevWorkflowRunHub> _hubContext;

    public DevWorkflowEventPublisher(IHubContext<DevWorkflowRunHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Guid runId, long sequence, DevWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.Group(DevWorkflowHubGroups.Run(runId))
                  .SendAsync(DevWorkflowHubEvents.Changed, new DevWorkflowChanged { RunId = runId, Seq = sequence, Kind = ToWireKind(kind) }, cancellationToken);

    /// <summary>
    ///     The wire spelling of a change kind, written out rather than derived from the enum name.
    /// </summary>
    /// <remarks>
    ///     The subscriber switches on these literals: a capitalised name matches no arm and silently stops updating
    ///     the view, so renaming an enum member must not be able to change the wire contract by accident.
    /// </remarks>
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
