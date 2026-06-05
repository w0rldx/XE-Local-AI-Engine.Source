namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Single source of truth for the chat reasoning-effort vocabulary and its canonical (lowercase)
///     normalization. Recognized values: the graded efforts <c>low</c>/<c>medium</c>/<c>high</c>, explicit
///     off (<c>none</c>), and the binary <c>on</c> sentinel — reason-by-default for a model that lacks the
///     Ollama <c>thinking</c> capability, for which the agent factory OMITS the <c>think</c> field so the
///     model's built-in (chat-template) reasoning runs. Blank or any unrecognized value normalizes to
///     <c>null</c> (unspecified).
///     Centralized so a new effort value is added in ONE place: the "on emits no reasoning" bug was the
///     <c>on</c> sentinel reaching the agent factory but being silently dropped to <c>null</c> by three
///     independent normalize/validate ladders (package builder, config hash, runtime-package validator),
///     which made the factory send <c>think:false</c> and suppress reasoning.
/// </summary>
public static class ReasoningEffortNormalizer
{
    /// <summary>
    ///     Returns the canonical lowercase effort (<c>none</c>/<c>on</c>/<c>low</c>/<c>medium</c>/<c>high</c>),
    ///     or <c>null</c> when the input is blank or unrecognized. Case-insensitive; trims surrounding whitespace.
    /// </summary>
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
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
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
