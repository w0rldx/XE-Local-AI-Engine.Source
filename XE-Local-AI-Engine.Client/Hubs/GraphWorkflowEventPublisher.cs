namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Pushes a committed graph-workflow change to that run's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
internal sealed class GraphWorkflowEventPublisher : IGraphWorkflowEventPublisher
{
    private readonly IHubContext<GraphWorkflowRunHub> _hubContext;

    public GraphWorkflowEventPublisher(IHubContext<GraphWorkflowRunHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.Group(GraphWorkflowHubGroups.Run(runId))
                   .SendAsync(GraphWorkflowHubEvents.Changed, new GraphWorkflowChanged
                   {
                       RunId = runId,
                       Seq = sequence,
                       Kind = ToWireKind(kind)
                   }, cancellationToken);

    /// <summary>
    ///     The wire spelling of a change kind, written out rather than derived from the enum name.
    /// </summary>
    /// <remarks>
    ///     The subscriber switches on these literals: a capitalised name matches no arm and silently stops updating
    ///     the view, so renaming an enum member must not be able to change the wire contract by accident.
    /// </remarks>
    private static string ToWireKind(GraphWorkflowChangeKind kind) =>
        kind switch
        {
            GraphWorkflowChangeKind.Run => "run",
            GraphWorkflowChangeKind.Node => "node",
            GraphWorkflowChangeKind.Gate => "gate",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown graph workflow change kind.")
        };
}
