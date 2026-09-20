namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Single source of truth for the chat reasoning-effort vocabulary and its canonical lowercase normalization.
/// </summary>
/// <remarks>
///     It recognizes the graded efforts, explicit <c>none</c>, the binary <c>on</c> sentinel — reason-by-default for
///     a model lacking the Ollama <c>thinking</c> capability, where the factory omits <c>think</c> so the template's
///     own reasoning runs — and <c>auto</c>, which the runner resolves per turn and never reaches a wire. Anything
///     else normalizes to <c>null</c>. Centralizing it keeps a sentinel from being dropped by one of several
///     independent ladders. Codex-only levels: <c>docs/wiki/05-chat.md</c>, "Reasoning effort + cloud clamp".
/// </remarks>
public static class ReasoningEffortNormalizer
{
    /// <summary>
    ///     Returns the canonical lowercase effort
    ///     (<c>none</c>/<c>on</c>/<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/<c>auto</c>), or
    ///     <c>null</c> when the input is blank or unrecognized. Case-insensitive; trims surrounding whitespace.
    /// </summary>
    /// <remarks>
    ///     <c>xhigh</c> survives normalization but falls back to <c>High</c> at the Codex wire, because the pinned
    ///     OpenAI 2.10.0 SDK has no <c>XHigh</c> member yet.
    /// </remarks>
    public static string? Normalize(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        // Upper-case for the comparison (CA1308 — never normalize to lower-case) but return the canonical
        // lower-case wire value the rest of the stack and the React client use.
        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "NONE" => "none",
            "ON" => "on",
            "MINIMAL" => "minimal",
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
            "XHIGH" => "xhigh",
            "AUTO" => "auto",
            _ => null
        };
    }

    /// <summary>
    ///     True when the value is blank (unspecified — allowed) or a recognized effort. A non-blank value that
    ///     does not normalize is invalid.
    /// </summary>
    public static bool IsValid(string? reasoningEffort)
    {
        return string.IsNullOrWhiteSpace(reasoningEffort) || Normalize(reasoningEffort) is not null;
    }
}
