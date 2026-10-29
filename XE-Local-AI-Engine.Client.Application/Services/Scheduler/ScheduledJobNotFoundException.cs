namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Identifies a missing scheduled job definition or run at the <see cref="IScheduledJobManagementService" />
///     boundary. A soft-deleted definition counts as missing for every action route; only the plain read still returns it.
/// </summary>
/// <remarks>
///     <c>ResourceNotFoundExceptionHandler</c> answers it with the surface's bodyless 404, so the Scheduler endpoints do
///     not branch on a null lookup themselves.
/// </remarks>
public sealed class ScheduledJobNotFoundException : ResourceNotFoundException
{
    public ScheduledJobNotFoundException(string message) : base(message)
    {
    }
}
