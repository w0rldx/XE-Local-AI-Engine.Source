namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

internal sealed class LocalToolOfferProvider : ILocalToolOfferProvider
{
    private readonly IReadOnlyList<AllowedToolDto> _allTools;
    private readonly IReadOnlyList<AllowedToolDto> _toolsWithoutAgentHome;
    private readonly HashSet<string> _toolCapableModels;

    public LocalToolOfferProvider(IAgentToolRegistry toolRegistry, IOptions<AgentHomeOptions> agentHomeOptions)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(agentHomeOptions);

        // The catalog is static for the process lifetime, so map it once. Each tool's Id is derived deterministically
        // from its name so the offer list is byte-identical across sends (the config hash ignores the Id, but a
        // stable Id keeps client-side rendering and equality predictable).
        _allTools =
        [
            .. toolRegistry.GetLocalChatToolDescriptors()
                           .Select(static descriptor => new AllowedToolDto
                           {
                               Id = DeriveDeterministicId(descriptor.Name),
                               Name = descriptor.Name,
                               Location = ToolLocation.ClientLocal,
                               ParameterSchema = descriptor.ParameterSchema,
                               RequiresApproval = descriptor.RequiresApproval
                           })
        ];

        // Precompute the capability-gated variant once: the same catalog minus run_in_agent_home, returned when the
        // active model is not tool-capable. The encrypted path stays server-gated and never reaches this provider.
        _toolsWithoutAgentHome =
        [
            .. _allTools.Where(static tool => !string.Equals(tool.Name, AgentHomeToolDefinition.ToolName, StringComparison.Ordinal))
        ];

        _toolCapableModels = new HashSet<string>(agentHomeOptions.Value.ToolCapableModels ?? [], StringComparer.Ordinal);
    }

    public IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId)
    {
        // run_in_agent_home is offered only to a tool-capable model. A null/unknown model id is treated as not capable,
        // so the high-risk tool is withheld rather than offered to a model that cannot drive it.
        var capable = activeModelId is not null && _toolCapableModels.Contains(activeModelId);
        return capable ? _allTools : _toolsWithoutAgentHome;
    }

    public IReadOnlyList<string> GetKnownToolNames()
    {
        // The full catalog (capable variant) is the canonical name set: it includes the capability-gated tools, so a
        // definition that references a high-risk tool is recognised as known even though an incapable model would not
        // be offered it at runtime.
        return [.. _allTools.Select(static tool => tool.Name)];
    }

    private static Guid DeriveDeterministicId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"local-tool:{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
