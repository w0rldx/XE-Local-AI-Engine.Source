namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

// ---------------------------------------------------------------------------
// HF token DTOs (Hugging Face token store — write-only value; never returned)
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/hf-token</c>. When <see cref="Token" /> is non-empty the token is stored encrypted at
///     rest; when it is null/empty the stored token is cleared (returns to anonymous access). The token is a secret: it is
///     NEVER returned by any endpoint, NEVER logged, and NEVER echoed in a response.
/// </summary>
public sealed class SetHfTokenRequest
{
    /// <summary>The Hugging Face access token to store; null/empty clears the stored token.</summary>
    public string? Token { get; init; }
}

/// <summary>
///     Response for the HF-token endpoints. Reports ONLY whether a token is currently configured — never the token value
///     itself.
/// </summary>
public sealed class HfTokenStatusResponse
{
    /// <summary>True when a token is currently stored (anonymous when false). The value itself is never exposed.</summary>
    public required bool HasToken { get; init; }
}
