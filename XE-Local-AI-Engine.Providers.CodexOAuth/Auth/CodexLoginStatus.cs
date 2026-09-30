namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>The state of the most recent / current Codex login attempt, surfaced by the <c>codex/status</c> endpoint.</summary>
public enum CodexLoginState
{
    /// <summary>No login has been started this process lifetime.</summary>
    None,

    /// <summary>A login is in flight: the authorize URL is available and the loopback callback is awaited.</summary>
    Pending,

    /// <summary>The most recent login completed and persisted a session.</summary>
    Succeeded,

    /// <summary>The most recent login failed (timed out, was superseded, or the exchange errored).</summary>
    Failed
}

/// <summary>
///     An immutable snapshot of the current login state for the status endpoint. Carries no token material.
/// </summary>
public sealed class CodexLoginStatus
{
    /// <summary>Current login lifecycle state.</summary>
    public required CodexLoginState State { get; init; }

    /// <summary>The authorize URL while <see cref="CodexLoginState.Pending" />; otherwise null.</summary>
    public required Uri? AuthorizeUrl { get; init; }

    /// <summary>Idle status used before any login has been attempted.</summary>
    public static CodexLoginStatus None { get; } = new()
    {
        State = CodexLoginState.None,
        AuthorizeUrl = null
    };
}
