namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using Microsoft.Extensions.AI;
using OpenAI.Responses;

/// <summary>
/// Forces <c>store=false</c> on the Codex Responses transport (plan D10 / M1 / pre-mortem §B-3).
///
/// <para>
/// <b>Phase 1.7 store=false decision (compile-gated):</b> the plan prefers
/// <c>ResponsesClient.AsIChatClientWithStoredOutputDisabled()</c> ONLY IF <c>Microsoft.Agents.AI.OpenAI</c>
/// compiles cleanly with the repo's existing <c>Microsoft.Agents.AI*</c> 1.6.2 package set. That package is
/// NOT present in the repo's package graph (the repo pins <c>Microsoft.Agents.AI.Hosting.OpenAI</c>, a
/// different package) and is not resolvable, so per the plan default we use the local
/// <see cref="ChatOptions.RawRepresentationFactory"/> mechanism instead. This carries no extra dependency and
/// is verified to compile against the pinned OpenAI 2.10.0 / Microsoft.Extensions.AI.OpenAI 10.6.0.
/// </para>
///
/// <para>
/// MEAI's Responses mapper uses the object returned by <see cref="ChatOptions.RawRepresentationFactory"/> as
/// the base <see cref="CreateResponseOptions"/>. Setting <see cref="CreateResponseOptions.StoredOutputEnabled"/>
/// to <see langword="false"/> and leaving <see cref="CreateResponseOptions.PreviousResponseId"/> /
/// <see cref="CreateResponseOptions.ConversationOptions"/> unset yields a request body that omits service-side
/// state. A Phase-3 body-assertion test proves the emitted body matches.
/// </para>
/// </summary>
public static class CodexResponseStoreDisabling
{
    /// <summary>
    /// Returns <paramref name="options"/> (or a new instance) with a <see cref="ChatOptions.RawRepresentationFactory"/>
    /// that disables service-side stored output for the Codex Responses path.
    /// </summary>
    public static ChatOptions WithStoredOutputDisabled(ChatOptions? options = null)
    {
        var result = options ?? new ChatOptions();
        result.RawRepresentationFactory = _ => new CreateResponseOptions
        {
            StoredOutputEnabled = false,
        };
        return result;
    }
}
