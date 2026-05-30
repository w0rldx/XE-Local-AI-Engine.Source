namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Surfaces the local tool catalog as the transport-level offer list. This is the clean Client-layer abstraction
///     over the internal <c>IAgentToolRegistry</c>: it converts the registry's tool descriptors into
///     <see cref="AllowedToolDto" />s (name + schema + approval flag, located <c>ClientLocal</c>) so the send and
///     regenerate paths can attach the offer list without depending on the AI.Agent assembly's internals.
/// </summary>
public interface ILocalToolOfferProvider
{
    /// <summary>
    ///     The catalog tools as offer-list DTOs for the given active model. The executables themselves are resolved by
    ///     the invocation factory from the registry by name; this list only travels in the runtime package for the
    ///     config hash and client display. High-risk catalog tools that require a tool-capable model (currently
    ///     <c>run_in_agent_home</c>) are omitted when <paramref name="activeModelId" /> is not in the worker's
    ///     tool-capable allow-list, so an incapable loopback model is never offered the tool (AgentHome locked decision
    ///     10). The encrypted path stays server-gated and does not call this seam.
    /// </summary>
    IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId);
}
