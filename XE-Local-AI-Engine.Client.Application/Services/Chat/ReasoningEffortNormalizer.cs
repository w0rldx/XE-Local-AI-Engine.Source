namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Single source of truth for the chat reasoning-effort vocabulary and its canonical (lowercase)
///     normalization. Recognized values: the graded efforts
///     <c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>, explicit off (<c>none</c>), and the
///     binary <c>on</c> sentinel — reason-by-default for a model that lacks the Ollama <c>thinking</c>
///     capability, for which the agent factory OMITS the <c>think</c> field so the model's built-in
///     (chat-template) reasoning runs. Blank or any unrecognized value normalizes to <c>null</c>
///     (unspecified).
///     <para>
///         <b>The <c>auto</c> value.</b> <c>auto</c> is a CONFIGURATION value like any other effort — persisted,
///         hashed and shown in the picker — but it NEVER reaches a provider wire. The invocation runner resolves it
///         per turn into a concrete <c>{model, effort, MaxOutputTokens}</c> before the agent definition is built
///         (see <c>IReasoningEffortDispatcher</c>), so the value a provider sees is always one of the graded/binary
///         levels above. It is recognized here so the ONE vocabulary stays the single source of truth; a leaked
///         <c>auto</c> reaching the agent factory behaves exactly as any unrecognized value does today.
///     </para>
///     Centralized so a new effort value is added in ONE place: the "on emits no reasoning" bug was the
///     <c>on</c> sentinel reaching the agent factory but being silently dropped to <c>null</c> by three
///     independent normalize/validate ladders (package builder, config hash, runtime-package validator),
///     which made the factory send <c>think:false</c> and suppress reasoning.
///     <para>
///         <b>Codex-only levels.</b> <c>minimal</c> and <c>xhigh</c> are members of the OpenAI Responses
///         reasoning-effort set and are only offered for Codex (cloud) models in the composer. They must NEVER
///         reach the Ollama <c>think</c> wire as a literal level (Ollama 400s on an unknown think level): the agent
///         factory maps both to <c>think:true</c> (reason) on the Ollama path and only the Codex boundary maps them
///         to <c>ResponseReasoningEffortLevel</c> (with <c>xhigh</c> falling back to <c>High</c> on the pinned
///         OpenAI 2.10.0 SDK, which exposes None/Minimal/Low/Medium/High but no XHigh member yet).
///     </para>
/// </summary>
public static class ReasoningEffortNormalizer
{
    /// <summary>
    ///     Returns the canonical lowercase effort
    ///     (<c>none</c>/<c>on</c>/<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/<c>auto</c>), or
    ///     <c>null</c> when the input is blank or unrecognized. Case-insensitive; trims surrounding whitespace.
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
