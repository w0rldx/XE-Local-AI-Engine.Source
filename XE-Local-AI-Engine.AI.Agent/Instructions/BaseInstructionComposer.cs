namespace XE_Local_AI_Engine.AI.Agent.Instructions;

/// <summary>
///     Joins the versioned base instruction scaffold ahead of a persona/task prompt. Shared by every composition site
///     (<c>AgentDefinitionResolver</c>, the sub-agent spawn default, the null-definition chat fallback) so the join
///     rule — a single blank line, defensively skipped when the scaffold is blank — lives in exactly one place.
/// </summary>
internal static class BaseInstructionComposer
{
    public static string Compose(string scaffold, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // A blank scaffold (defensive — the embedded resource is never blank in practice) composes to the body
        // unchanged rather than leaving a leading blank line.
        return string.IsNullOrWhiteSpace(scaffold) ? body : $"{scaffold.TrimEnd()}\n\n{body}";
    }
}
