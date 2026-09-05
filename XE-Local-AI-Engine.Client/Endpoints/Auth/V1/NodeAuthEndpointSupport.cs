namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using System.Globalization;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     The single token-issuing response shared by login and refresh: both mint the same pair, so both must write the
///     refresh cookie and the access-token body — or neither. A partial result (an access token without its refresh
///     cookie, or a stale cookie left behind on failure) is an authentication bug, which is why the cookie write and
///     the 200/401 decision are one operation rather than two steps each caller repeats.
/// </summary>
internal static class NodeAuthEndpointSupport
{
    public static async Task SendTokenResultAsync<TRequest>(ResponseSender<TRequest, NodeAccessTokenResponse> send,
        NodeAuthTokenResult result,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!TryWriteRefreshCookie(send.HttpContext.Response, result))
        {
            NodeAuthCookie.ClearRefreshToken(send.HttpContext.Response);

            // The one 401 that says WHY. Only login can produce it (a refresh has no account to lock), so the branch is
            // inert on the refresh path — but the cookie clear and the 401 stay one operation, which is why it lives
            // here rather than in the login endpoint.
            if (result.LockedOutRetryAfterSeconds is { } retryAfterSeconds)
            {
                send.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                await send.ResultAsync(Results.Json(new NodeLoginLockedOutResponse
                {
                    Message = "Too many failed sign-in attempts. This account is temporarily locked.",
                    RetryAfterSeconds = retryAfterSeconds
                }, statusCode: StatusCodes.Status401Unauthorized)).ConfigureAwait(false);
                return;
            }

            await send.UnauthorizedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await send.OkAsync(new NodeAccessTokenResponse
        {
            AccessToken = result.AccessToken!,
            ExpiresAtUtc = result.AccessTokenExpiresAtUtc!.Value
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryWriteRefreshCookie(HttpResponse response, NodeAuthTokenResult result)
    {
        if (!result.Succeeded
            || string.IsNullOrWhiteSpace(result.AccessToken)
            || !result.AccessTokenExpiresAtUtc.HasValue
            || string.IsNullOrWhiteSpace(result.RefreshToken)
            || !result.RefreshTokenExpiresAtUtc.HasValue)
        {
            return false;
        }

        NodeAuthCookie.AppendRefreshToken(response, result.RefreshToken, result.RefreshTokenExpiresAtUtc.Value);
        return true;
    }
}
