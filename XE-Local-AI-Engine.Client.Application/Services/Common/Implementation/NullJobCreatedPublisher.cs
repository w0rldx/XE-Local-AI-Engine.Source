namespace XE_Local_AI_Engine.Client.Services.Common.Implementation;

/// <summary>No-op <see cref="IJobCreatedPublisher" /> for hosts with no SignalR hub wired.</summary>
public sealed class NullJobCreatedPublisher : IJobCreatedPublisher
{
    public Task PublishCreatedAsync(CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
