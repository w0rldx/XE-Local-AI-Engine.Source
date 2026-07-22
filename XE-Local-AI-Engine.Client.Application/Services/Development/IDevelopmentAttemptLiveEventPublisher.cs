namespace XE_Local_AI_Engine.Client.Services.Development;

public interface IDevelopmentAttemptLiveEventPublisher
{
    Task PublishAsync(DevelopmentAttemptLiveUpdate update, CancellationToken cancellationToken = default);
}

internal sealed class NullDevelopmentAttemptLiveEventPublisher : IDevelopmentAttemptLiveEventPublisher
{
    public Task PublishAsync(DevelopmentAttemptLiveUpdate update, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
