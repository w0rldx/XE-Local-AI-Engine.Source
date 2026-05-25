namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Endpoints.Common;

public static class NodeAuthCookie
{
    public const string RefreshCookieName = "node_rt";

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
