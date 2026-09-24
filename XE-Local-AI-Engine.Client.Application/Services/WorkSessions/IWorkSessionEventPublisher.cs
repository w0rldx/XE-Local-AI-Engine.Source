namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>What changed on a work session, as the hub's subscribers see it.</summary>
public enum WorkSessionChangeKind
{
    Status,
    Step,
    Task,
    Finding,
    Artifact,
    Checkpoint
}

/// <summary>
///     Announces a committed work-session change to whoever is watching the session, AFTER the commit that allocated
///     <c>sequence</c>, so a subscriber replaying from the watermark can never miss the row it names.
/// </summary>
/// <remarks>
///     Three callers publish: the session service (Status on start and resume), the supervisor (Status, Step,
///     Checkpoint, and Finding for a park timeout's open question) and the four state tool handlers from inside the
///     invocation loop (Task, Finding, Artifact).
/// </remarks>
public interface IWorkSessionEventPublisher
{
    Task PublishAsync(Guid sessionId, long sequence, WorkSessionChangeKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
///     The publisher a host without the work-session hub resolves. Registered with <c>TryAddSingleton</c> so the hub's
///     own registration wins wherever it is present, exactly as the tool-approval policy pair does.
/// </summary>
internal sealed class NoOpWorkSessionEventPublisher : IWorkSessionEventPublisher
{
    public Task PublishAsync(Guid sessionId, long sequence, WorkSessionChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
