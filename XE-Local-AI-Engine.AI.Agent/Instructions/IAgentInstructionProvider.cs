namespace XE_Local_AI_Engine.AI.Agent.Instructions;

internal interface IAgentInstructionProvider
{
    string GetLocalChatInstructions();

    /// <summary>
    ///     The app-owned base instruction scaffold (identity/grounding/tool/output discipline) prepended ahead of a
    ///     persona's own Instructions when composing a resolved prompt, unless the definition opts out. A scaffold text
    ///     edit needs no explicit version marker: the runtime package config hash covers the final composed resolved
    ///     prompt, so changing the scaffold changes the hash on its own.
    /// </summary>
    string GetBaseScaffold();

    /// <summary>
    ///     The scaffold-composed default chat system prompt (<see cref="GetBaseScaffold" /> ahead of
    ///     <see cref="GetLocalChatInstructions" />) for the true null-definition fallback — a conversation with no
    ///     bound agent at all. Mirrors what <c>AgentDefinitionResolver</c> composes for a bound, non-opted-out
    ///     definition, so an unbound send gets the same scaffold coverage as a bound one.
    /// </summary>
    string GetDefaultChatSystemPrompt();
}
