namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Shared helpers for the execute endpoints: resolving the originating hub connection id (so a disconnect cancels
///     the run) and the 409 CapReached result. The connection id is supplied by the React client via a header — it is
///     the SignalR connection that subscribed for this run's events.
/// </summary>
internal static class PreviewExecuteHelper
{
    /// <summary>Header the React client sets to its SignalR connection id when starting a run.</summary>
    public const string ConnectionIdHeader = "X-Preview-Connection-Id";

    public static string? ResolveConnectionId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Request.Headers.TryGetValue(ConnectionIdHeader, out var values))
        {
            var value = values.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static IResult CapReached(PreviewWorkflowCapReachedException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Results.Conflict(new
        {
            conflictType = "CapReached",
            message = exception.Message,
            maxConcurrentRuns = exception.MaxConcurrentRuns
        });
    }

    public static IResult ModelCapExceeded(PreviewWorkflowModelCapExceededException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Results.Conflict(new
        {
            conflictType = "ModelCapExceeded",
            message = exception.Message,
            distinctModelCount = exception.DistinctModelCount,
            maxLoadedProcesses = exception.MaxLoadedProcesses
        });
    }
}
