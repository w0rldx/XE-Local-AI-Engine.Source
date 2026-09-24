namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentAttemptLiveEventPublisher : IDevelopmentAttemptLiveEventPublisher
{
    private readonly IHubContext<DevelopmentAttemptHub> _hubContext;

    public DevelopmentAttemptLiveEventPublisher(IHubContext<DevelopmentAttemptHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(DevelopmentAttemptLiveUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return _hubContext.Clients.Group(DevelopmentAttemptHubGroups.Attempt(update.ProjectId, update.AttemptId))
                          .SendAsync("developmentAttemptUpdate", update, cancellationToken);
    }
}
