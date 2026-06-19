namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// Single source of truth for the Codex header contract. The wire-contract test
/// binds to these constants. Account-id header is <c>chatgpt-account-id</c> (Codex CLI), NOT
/// <c>openai-</c>-prefixed — the prefix may break account-scoped auth.
///
/// Verified against the Codex CLI source path <c>codex-rs/core/src/client.rs</c>:
/// the v0 SSE Responses path sends the Always (auth) headers plus the minimal HTTP/SSE subset
/// (<c>originator</c>, <c>User-Agent</c>). The WebSocket-only <c>OpenAI-Beta: responses_websockets</c>
/// header is intentionally NOT defined/sent here.
/// </summary>
internal static class CodexHeaders
{
    /// <summary>Bearer access token. Always sent. The SDK's dummy "unused" Authorization is stripped first.</summary>
    public const string Authorization = "Authorization";

    /// <summary>Account scope from the JWT <c>chatgpt_account_id</c> claim. Always sent.</summary>
    public const string AccountId = "chatgpt-account-id";

    /// <summary>Client family identifier. Sent on the HTTP/SSE Responses path.</summary>
    public const string Originator = "originator";

    /// <summary>Client user agent. Sent on the HTTP/SSE Responses path.</summary>
    public const string UserAgent = "User-Agent";

    /// <summary>
    /// Per-request correlation id. LIVE-CORRECTNESS: the working opencode reference client sets a fresh
    /// <c>session-id</c> (GUID) on each Responses call. Lower-confidence requirement but matches the reference
    /// and is harmless.
    /// </summary>
    public const string SessionId = "session-id";
}
