namespace XE_Local_AI_Engine.AI.Agent.Instructions;

internal interface IAgentInstructionProvider
{
    string GetLocalChatInstructions();

    /// <summary>
    ///     The versioned, app-owned base instruction scaffold (identity/grounding/tool/output discipline) prepended
    ///     ahead of a persona's own Instructions when composing a resolved prompt, unless the definition opts out. See
    ///     <see cref="ScaffoldVersion" />.
    /// </summary>
    string GetBaseScaffold();

    /// <summary>
    ///     Version of the text returned by <see cref="GetBaseScaffold" />; bumped whenever its content changes. NOT
    ///     itself folded into the runtime package config hash — the hash already covers the final composed resolved
    ///     prompt, so a scaffold text edit changes the hash on its own. Exposed for diagnostics only.
    /// </summary>
    int ScaffoldVersion { get; }

    /// <summary>
    ///     The scaffold-composed default chat system prompt (<see cref="GetBaseScaffold" /> ahead of
    ///     <see cref="GetLocalChatInstructions" />) for the true null-definition fallback — a conversation with no
    ///     bound agent at all. Mirrors what <c>AgentDefinitionResolver</c> composes for a bound, non-opted-out
    ///     definition, so an unbound send gets the same scaffold coverage as a bound one.
    /// </summary>
    string GetDefaultChatSystemPrompt();
}
