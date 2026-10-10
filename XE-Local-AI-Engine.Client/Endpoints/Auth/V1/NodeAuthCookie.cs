namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Local API contract type for node auth cookie.
/// </summary>
public static class NodeAuthCookie
{
    public const string RefreshCookieName = "node_rt";

    /// <summary>The vault unlock's one-time ticket (ADR 0018), scoped and flagged like the refresh cookie.</summary>
    public const string UnlockTicketCookieName = "node_ut";

    public static string RefreshCookiePath => $"/{LocalApiRoutes.Prefix}/auth";

    public static void AppendRefreshToken(HttpResponse response, string refreshToken, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        response.Cookies.Append(RefreshCookieName, refreshToken, CreateRefreshCookieOptions(expiresAtUtc));
    }

    public static void ClearRefreshToken(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(RefreshCookieName, CreateRefreshCookieOptions(DateTimeOffset.UnixEpoch.UtcDateTime));
    }

    public static void AppendUnlockTicket(HttpResponse response, string ticket, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);

        response.Cookies.Append(UnlockTicketCookieName, ticket, CreateRefreshCookieOptions(expiresAtUtc));
    }

    public static void ClearUnlockTicket(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(UnlockTicketCookieName, CreateRefreshCookieOptions(DateTimeOffset.UnixEpoch.UtcDateTime));
    }

    private static CookieOptions CreateRefreshCookieOptions(DateTime expiresAtUtc)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)),
            IsEssential = true
        };
    }
}
