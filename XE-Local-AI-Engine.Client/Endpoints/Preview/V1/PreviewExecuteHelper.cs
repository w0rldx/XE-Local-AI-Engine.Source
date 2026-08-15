namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

/// <summary>
///     Shared helper for the execute endpoints: resolves the originating hub connection id (so a disconnect cancels
///     the run). The connection id is supplied by the React client via a header — it is the SignalR connection that
///     subscribed for this run's events. The cap 409s are NOT built here: the execution service's cap exceptions
///     reach the global ConflictExceptionHandler.
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
}
