namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using Microsoft.Extensions.AI;

/// <summary>
/// Cloud-provider factory contract for the Codex OAuth transport, parallel to
/// <c>IAzureFoundryChatClientFactory</c> (plan §6.1 / D3). Builds the inner <see cref="IChatClient"/> over the
/// Codex Responses endpoint with OAuth injected via <c>CodexAuthHandler</c>. It is transport only — no model
/// lifecycle (no pull/warm/unload), and it does NOT implement <c>ILocalModelProvider</c> (M7 dissolved).
/// </summary>
public interface ICodexOAuthChatClientFactory
{
    /// <summary>The declared capability matrix for the Codex provider (plan §7).</summary>
    AgentModelCapabilities Capabilities { get; }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> for the supplied account-scoped model id (or the configured default
    /// when <paramref name="modelId"/> is null/blank). Forces <c>store=false</c>. The returned client shares the
    /// factory's single <see cref="HttpClient"/>; disposing it does NOT dispose the shared client (M3).
    /// </summary>
    IChatClient Create(string? modelId = null);
}
