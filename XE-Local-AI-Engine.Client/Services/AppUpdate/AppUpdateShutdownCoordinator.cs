namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Stops the desktop host only after ASP.NET Core has completed the successful apply response. Velopack's updater is
///     already waiting for this process to exit at that point, so graceful shutdown hands control to it without
///     aborting the response that tells React to start health polling.
/// </summary>
public sealed class AppUpdateShutdownCoordinator
{
    private readonly IHostApplicationLifetime _applicationLifetime;

    public AppUpdateShutdownCoordinator(IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        _applicationLifetime = applicationLifetime;
    }

    public void StopAfterResponseCompleted(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.OnCompleted(static state =>
            {
                ((IHostApplicationLifetime)state).StopApplication();
                return Task.CompletedTask;
            },
            _applicationLifetime);
    }
}
