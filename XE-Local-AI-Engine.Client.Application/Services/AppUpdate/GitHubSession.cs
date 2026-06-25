namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     The persisted GitHub sign-in: the user access token plus the GitHub login it belongs to. There is intentionally
///     NO refresh token and NO expiry (decision #9 — user-token expiration is off; a GitHub-side revoke surfaces as a
///     401 on the next check and is handled as <c>reauthRequired</c>). The token is a secret: it is stored encrypted at
///     rest, exposed only to the update source as an <c>Authorization: Bearer</c> header, and never logged, never placed
///     in a DTO/exception, and never returned to React.
/// </summary>
/// <param name="AccessToken">The GitHub user access token (e.g. <c>ghu_…</c>).</param>
/// <param name="Login">The GitHub login (username) the token authenticated, for display only.</param>
public sealed record GitHubSession(string AccessToken, string Login);
