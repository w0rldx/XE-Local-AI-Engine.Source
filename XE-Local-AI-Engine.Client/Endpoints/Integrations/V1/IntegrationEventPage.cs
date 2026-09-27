namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

/// <summary>
///     The one page bound both event routes share.
/// </summary>
/// <remarks>
///     It lives here rather than in either route because the operator's timeline and the external recovery poll must
///     page identically: a caller that learns the page size from one and uses it against the other would otherwise
///     silently skip rows.
/// </remarks>
public static class IntegrationEventPage
{
    /// <summary>What a caller that names no limit gets.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The ceiling. A bounded page is the point: an execution's timeline is unbounded in principle.</summary>
    public const int MaxLimit = 500;

    public static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultLimit, min: 1, MaxLimit);
}
