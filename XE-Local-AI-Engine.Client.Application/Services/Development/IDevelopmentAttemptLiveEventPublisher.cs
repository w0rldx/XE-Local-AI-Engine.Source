namespace XE_Local_AI_Engine.Client.Services.Development;

public interface IDevelopmentAttemptLiveEventPublisher
{
    Task PublishAsync(DevelopmentAttemptLiveUpdate update, CancellationToken cancellationToken = default);
}
