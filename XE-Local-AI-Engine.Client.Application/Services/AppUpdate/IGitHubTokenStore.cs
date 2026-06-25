namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Persistence boundary for the encrypted GitHub user access token used to read private-repo releases for app
///     self-update. The token is a secret: it is stored encrypted at rest and exposed only to the update source, which
///     sets it as an <c>Authorization: Bearer</c> header. It is never logged, never placed in exceptions, never returned
///     to React, and never in any DTO. There is no refresh token and no expiry (decision #9).
/// </summary>
public interface IGitHubTokenStore
{
    /// <summary>Loads the stored session, or <see langword="null" /> when none is configured (signed out).</summary>
    Task<GitHubSession?> GetSessionAsync(CancellationToken ct);

    /// <summary>Persists <paramref name="session" />, encrypted at rest, replacing any existing session.</summary>
    Task SetSessionAsync(GitHubSession session, CancellationToken ct);

    /// <summary>Clears any stored session (sign out locally).</summary>
    Task ClearSessionAsync(CancellationToken ct);

    /// <summary>Returns whether a session is currently stored, without exposing the token value.</summary>
    Task<bool> HasSessionAsync(CancellationToken ct);
}
