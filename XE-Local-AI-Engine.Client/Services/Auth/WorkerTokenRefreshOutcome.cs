namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Describes the result of a worker access-token refresh attempt.
/// </summary>
public enum WorkerTokenRefreshOutcome
{
    /// <summary>
    ///     The access token was refreshed successfully and stored.
    /// </summary>
    Success = 0,

    /// <summary>
    ///     The refresh attempt failed for a recoverable reason (network error, 5xx, malformed response).
    ///     The caller should keep retrying with the existing credentials.
    /// </summary>
    TransientFailure = 1,

    /// <summary>
    ///     The Central Platform permanently rejected the worker credentials (401/403/404) or no refresh
    ///     token is available. The worker must stop reconnecting and re-pair.
    /// </summary>
    CredentialsRevoked = 2
}
