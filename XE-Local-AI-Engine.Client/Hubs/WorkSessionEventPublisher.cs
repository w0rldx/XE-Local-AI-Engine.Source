namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Pushes a committed work-session change to that session's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
internal sealed class WorkSessionEventPublisher : IWorkSessionEventPublisher
{
    private readonly IHubContext<WorkSessionHub> _hubContext;

    public WorkSessionEventPublisher(IHubContext<WorkSessionHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Guid sessionId, long sequence, WorkSessionChangeKind kind, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.Group(WorkSessionHubGroups.Session(sessionId))
                   .SendAsync(WorkSessionHubEvents.Changed, new WorkSessionChanged
                   {
                       SessionId = sessionId,
                       Seq = sequence,
                       Kind = ToWireKind(kind)
                   }, cancellationToken);

    /// <summary>
    ///     The wire spelling of a change kind, written out rather than derived from the enum name.
    /// </summary>
    /// <remarks>
    ///     The subscriber switches on these literals: a capitalised name matches no arm and silently stops updating
    ///     the pane, so renaming an enum member must not be able to change the wire contract by accident.
    /// </remarks>
    private static string ToWireKind(WorkSessionChangeKind kind) =>
        kind switch
        {
            WorkSessionChangeKind.Status => "status",
            WorkSessionChangeKind.Step => "step",
            WorkSessionChangeKind.Task => "task",
            WorkSessionChangeKind.Finding => "finding",
            WorkSessionChangeKind.Artifact => "artifact",
            WorkSessionChangeKind.Checkpoint => "checkpoint",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown work-session change kind.")
        };
}
