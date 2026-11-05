namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The publisher a host without the work-session hub resolves. Registered with <c>TryAddSingleton</c> so the hub's
///     own registration wins wherever it is present, exactly as the tool-approval policy pair does.
/// </summary>
internal sealed class NoOpWorkSessionEventPublisher : IWorkSessionEventPublisher
{
    public Task PublishAsync(Guid sessionId, long sequence, WorkSessionChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
