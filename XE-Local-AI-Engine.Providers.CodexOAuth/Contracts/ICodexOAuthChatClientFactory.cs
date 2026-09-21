namespace XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;

using Microsoft.Extensions.AI;

/// <summary>
///     Cloud-provider factory contract for the Codex OAuth transport, parallel to
///     <c>IAzureFoundryChatClientFactory</c>: builds the inner <see cref="IChatClient" /> over the Codex Responses
///     endpoint with OAuth injected through <c>CodexAuthHandler</c>.
/// </summary>
/// <remarks>
///     Transport only — no model lifecycle, no pull, warm or unload — and it does NOT implement
///     <c>ILocalModelProvider</c>.
/// </remarks>
public interface ICodexOAuthChatClientFactory
{
    /// <summary>The declared capability matrix for the Codex provider.</summary>
    AgentModelCapabilities Capabilities { get; }

    /// <summary>
    ///     Builds an <see cref="IChatClient" /> for the supplied account-scoped model id (or the configured default
    ///     when <paramref name="modelId" /> is null/blank). Forces <c>store=false</c>. The returned client shares the
    ///     factory's single <see cref="HttpClient" />; disposing it does NOT dispose the shared client.
    /// </summary>
    IChatClient Create(string? modelId = null);
}
