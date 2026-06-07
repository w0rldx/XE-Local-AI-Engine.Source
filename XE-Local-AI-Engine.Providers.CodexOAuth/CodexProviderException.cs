namespace XE_Local_AI_Engine.Providers.CodexOAuth;

/// <summary>
/// Categories of Codex provider failures surfaced to callers (plan §8 Phase 3.4). Distinct from auth-flow
/// failures (<see cref="Auth.CodexAuthException"/>), which map to <see cref="AuthRequired"/> / <see cref="RefreshFailed"/>.
/// </summary>
public enum CodexProviderErrorKind
{
    /// <summary>No valid Codex session; interactive login is required.</summary>
    AuthRequired,

    /// <summary>A token refresh failed; re-login is required.</summary>
    RefreshFailed,

    /// <summary>The backend returned a rate-limit response (HTTP 429).</summary>
    RateLimited,

    /// <summary>A transport/network error reaching the Codex backend.</summary>
    Transport,

    /// <summary>The requested model id is unavailable for this account.</summary>
    ModelUnavailable,
}

/// <summary>
/// A typed Codex provider error. Messages must never contain token values or authorization headers (plan §9).
/// </summary>
public sealed class CodexProviderException : Exception
{
    public CodexProviderException(CodexProviderErrorKind kind, string message)
        : base(message) => Kind = kind;

    public CodexProviderException(CodexProviderErrorKind kind, string message, Exception innerException)
        : base(message, innerException) => Kind = kind;

    /// <summary>The category of failure.</summary>
    public CodexProviderErrorKind Kind { get; }
}
