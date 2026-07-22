namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentAttemptLiveEventPublisher(IHubContext<DevelopmentAttemptHub> hubContext)
    : IDevelopmentAttemptLiveEventPublisher
{
    public Task PublishAsync(DevelopmentAttemptLiveUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return hubContext.Clients.Group(DevelopmentAttemptHubGroups.Attempt(update.ProjectId, update.AttemptId))
                         .SendAsync("developmentAttemptUpdate", update, cancellationToken);
    }
}
