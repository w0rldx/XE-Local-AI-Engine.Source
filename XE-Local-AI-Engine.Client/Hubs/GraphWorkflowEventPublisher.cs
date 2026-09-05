namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Pushes a committed graph-workflow change to that run's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
internal sealed class GraphWorkflowEventPublisher(IHubContext<GraphWorkflowRunHub> hubContext) : IGraphWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(GraphWorkflowHubGroups.Run(runId))
                  .SendAsync(GraphWorkflowHubEvents.Changed, new GraphWorkflowChanged(runId, sequence, ToWireKind(kind)), cancellationToken);

    /// <summary>
    ///     The wire spelling of a change kind, written out rather than derived from the enum name. The subscriber
    ///     switches on these literals: a capitalised name matches no arm and silently stops updating the view, and
    ///     renaming an enum member must not be able to change the wire contract by accident.
    /// </summary>
    private static string ToWireKind(GraphWorkflowChangeKind kind) =>
        kind switch
        {
            GraphWorkflowChangeKind.Run => "run",
            GraphWorkflowChangeKind.Node => "node",
            GraphWorkflowChangeKind.Gate => "gate",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown graph workflow change kind.")
        };
}
