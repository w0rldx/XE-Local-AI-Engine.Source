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
    private const string BuiltinSource = "builtin";
    private const string McpSourcePrefix = "mcp:";
    private const string McpNamePrefix = "mcp__";

    // The built-in catalog is static for the process lifetime, so precompute its three projections once. The MCP part
    // is dynamic (servers connect/disconnect) and is read live from the registry on each call, then merged in.
    private readonly IReadOnlyList<AllowedToolDto> _builtinAllTools;
    private readonly IReadOnlyList<LocalToolCatalogEntry> _builtinCatalogEntries;
    private readonly IReadOnlyList<string> _builtinNames;
    private readonly IReadOnlyList<AllowedToolDto> _builtinWithoutAgentHome;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly HashSet<string> _toolCapableModels;

    public LocalToolOfferProvider(IAgentToolRegistry toolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        IOptions<AgentHomeOptions> agentHomeOptions)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
        ArgumentNullException.ThrowIfNull(agentHomeOptions);

        var builtinDescriptors = toolRegistry.GetLocalChatToolDescriptors();

        // Each tool's Id is derived deterministically from its name so the offer list is byte-identical across sends
        // (the config hash ignores the Id, but a stable Id keeps client-side rendering and equality predictable).
        _builtinAllTools =
        [
            .. builtinDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval))
        ];

        // Precompute the capability-gated variant once: the built-ins minus run_in_agent_home, returned when the active
        // model is not tool-capable. The encrypted path stays server-gated and never reaches this provider.
        _builtinWithoutAgentHome =
        [
            .. _builtinAllTools.Where(static tool => !string.Equals(tool.Name, AgentHomeToolDefinition.ToolName, StringComparison.Ordinal))
        ];

        _builtinCatalogEntries =
        [
            .. builtinDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = BuiltinSource
            })
        ];

        _builtinNames = [.. builtinDescriptors.Select(static descriptor => descriptor.Name)];

        _toolCapableModels = new HashSet<string>(agentHomeOptions.Value.ToolCapableModels ?? [], StringComparer.Ordinal);
    }

    public IReadOnlyList<AllowedToolDto> GetOfferedTools(string? activeModelId)
    {
        // High-risk tools (run_in_agent_home and every MCP tool) are offered only to a tool-capable model. A
        // null/unknown model id is treated as not capable, so those tools are withheld rather than offered to a model
        // that cannot drive them. The MCP part is read live and sorted so the same catalog state yields a byte-identical
        // offer (stable config hash).
        var capable = activeModelId is not null && _toolCapableModels.Contains(activeModelId);
        if (!capable)
        {
            return _builtinWithoutAgentHome;
        }

        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return _builtinAllTools;
        }

        return
        [
            .. _builtinAllTools,
            .. mcpDescriptors.Select(static descriptor => ToOfferDto(descriptor.Name, descriptor.ParameterSchema, descriptor.RequiresApproval))
        ];
    }

    public IReadOnlyList<string> GetKnownToolNames()
    {
        // The full catalog name set: every built-in (capable variant, so the capability-gated tools are still known)
        // plus every live MCP tool. CRUD validation uses this to warn (not fail) on an unknown name.
        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return _builtinNames;
        }

        return
        [
            .. _builtinNames,
            .. mcpDescriptors.Select(static descriptor => descriptor.Name)
        ];
    }

    public IReadOnlyList<LocalToolCatalogEntry> GetKnownTools()
    {
        // The full catalog as rich entries, UNGATED by model (the agent form shows all tools regardless of the active
        // model). Built-ins are precomputed; MCP entries are read live and tagged with their originating server slug.
        var mcpDescriptors = _mcpToolRegistry.GetDescriptors();
        if (mcpDescriptors.Count == 0)
        {
            return _builtinCatalogEntries;
        }

        return
        [
            .. _builtinCatalogEntries,
            .. mcpDescriptors.Select(static descriptor => new LocalToolCatalogEntry
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                RequiresApproval = descriptor.RequiresApproval,
                Source = ToMcpSource(descriptor.Name)
            })
        ];
    }

    private static AllowedToolDto ToOfferDto(string name, string? parameterSchema, bool requiresApproval)
    {
        return new AllowedToolDto
        {
            Id = DeriveDeterministicId(name),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = parameterSchema,
            RequiresApproval = requiresApproval
        };
    }

    /// <summary>
    ///     Derives the catalog source tag for an MCP tool from its qualified name <c>mcp__{slug}__{tool}</c>, yielding
    ///     <c>mcp:{slug}</c> so the UI can group tools by their originating server. A name that does not match the
    ///     expected shape falls back to the bare <c>mcp</c> tag.
    /// </summary>
    private static string ToMcpSource(string qualifiedName)
    {
        if (qualifiedName.StartsWith(McpNamePrefix, StringComparison.Ordinal))
        {
            var rest = qualifiedName[McpNamePrefix.Length..];
            var separatorIndex = rest.IndexOf("__", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                return McpSourcePrefix + rest[..separatorIndex];
            }
        }

        return "mcp";
    }

    private static Guid DeriveDeterministicId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"local-tool:{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
