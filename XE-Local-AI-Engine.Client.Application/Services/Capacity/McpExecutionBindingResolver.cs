namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Produces the repeatable, keyed execution binding used exclusively by inbound MCP delegation. General saved
///     agents and bare models are model-visible tool-less. Only the forge-proof seeded Coder definition may receive the
///     three workspace read tools, and only when each resolved descriptor is still categorized <see cref="ToolCategory.ReadLocal" />.
/// </summary>
internal sealed class McpExecutionBindingResolver : IMcpExecutionBindingResolver
{
    private const int FingerprintVersion = 1;

    private const string DefaultSubAgentPersonaInstructions =
        "You are a focused sub-agent. Complete the delegated task and return a concise result.";

    private static readonly HashSet<string> CoderToolNames =
    [
        CoderToolDefinition.ListFilesToolName,
        CoderToolDefinition.ReadFileToolName,
        CoderToolDefinition.SearchTextToolName
    ];

    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    private readonly IAgentDefinitionStore _definitionStore;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly INodeSqliteKeyHolder _nodeKey;

    public McpExecutionBindingResolver(IAgentDefinitionStore definitionStore,
        IAgentDefinitionResolver agentDefinitionResolver,
        IGgufModelStore ggufModelStore,
        IAgentInstructionProvider instructionProvider,
        IModelCapabilityResolver modelCapabilityResolver,
        INodeSqliteKeyHolder nodeKey)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _agentDefinitionResolver = agentDefinitionResolver ?? throw new ArgumentNullException(nameof(agentDefinitionResolver));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _nodeKey = nodeKey ?? throw new ArgumentNullException(nameof(nodeKey));
    }

    public async Task<McpExecutionBindingResolution> ResolveAsync(McpExecutionBindingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasAgent = !string.IsNullOrWhiteSpace(request.AgentKey);
        var hasModel = !string.IsNullOrWhiteSpace(request.ModelId);
        if (hasAgent == hasModel || (!hasAgent && !string.IsNullOrWhiteSpace(request.ModelOverrideId)))
        {
            return Reject(McpExecutionFailureCodes.InvalidRequest, "Cannot run: provide exactly one of agent or model.");
        }

        return hasModel
            ? await ResolveBareModelAsync(request, cancellationToken).ConfigureAwait(false)
            : await ResolveSavedAgentAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpExecutionBindingResolution> ResolveBareModelAsync(McpExecutionBindingRequest request, CancellationToken cancellationToken)
    {
        var modelId = request.ModelId!;
        if (!await _ggufModelStore.ExistsAsync(modelId, cancellationToken).ConfigureAwait(false))
        {
            return Reject(McpExecutionFailureCodes.ModelNotAvailable, "Cannot run: the requested local model is not available.");
        }

        var instructions = string.IsNullOrWhiteSpace(request.Instructions)
            ? BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), DefaultSubAgentPersonaInstructions)
            : request.Instructions;
        var binding = CreateBinding(modelId,
            instructions,
            agentDefinitionId: null,
            agentDefinitionVersion: null,
            [],
            reasoningEffort: null,
            supportsThinking: false);
        return McpExecutionBindingResolution.Success(binding);
    }

    private async Task<McpExecutionBindingResolution> ResolveSavedAgentAsync(McpExecutionBindingRequest request, CancellationToken cancellationToken)
    {
        var definition = await ResolveDefinitionAsync(request.AgentKey!, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return Reject(McpExecutionFailureCodes.AgentNotFound, "Cannot run: the requested saved agent was not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.ModelOverrideId) && !string.IsNullOrWhiteSpace(definition.ModelProfile))
        {
            return Reject(McpExecutionFailureCodes.ModelOverrideNotAllowed, "Cannot run: a model override is allowed only for an agent without a pinned model.");
        }

        var modelId = definition.ModelProfile ?? request.ModelOverrideId;
        if (string.IsNullOrWhiteSpace(modelId)
            || !await _ggufModelStore.ExistsAsync(modelId, cancellationToken).ConfigureAwait(false))
        {
            return Reject(McpExecutionFailureCodes.ModelNotAvailable, "Cannot run: the agent's local model is not available.");
        }

        var (supportsThinking, supportsTools, _) = await _modelCapabilityResolver.ResolveAsync(modelId, cancellationToken).ConfigureAwait(false);
        var resolved = await _agentDefinitionResolver.ResolveAsync(definition.Id,
                                                         modelId,
                                                         supportsTools: supportsTools,
                                                         honorModelProfile: !string.IsNullOrWhiteSpace(definition.ModelProfile),
                                                         cancellationToken: cancellationToken)
                                                     .ConfigureAwait(false);
        if (resolved is null || resolved.AgentDefinitionVersion != definition.Version)
        {
            return Reject(McpExecutionFailureCodes.AgentConfigChanged, "Cannot run: the saved agent configuration changed while it was being resolved.");
        }

        var isSeededCoder = definition.Source == AgentDefinitionSource.Seeded
                            && string.Equals(definition.SeedSlug, AgentDefaults.CoderAgentSeedSlug, StringComparison.Ordinal);
        IReadOnlyList<AllowedToolDto> allowedTools = [];
        if (isSeededCoder && !TryProjectExactCoderTools(resolved.AllowedTools, out allowedTools))
        {
            return Reject(McpExecutionFailureCodes.AgentConfigChanged,
                "Cannot run: the saved Coder capability configuration is incomplete or unsafe.");
        }

        var binding = CreateBinding(modelId,
            resolved.ResolvedSystemPrompt,
            definition.Id,
            definition.Version,
            allowedTools,
            resolved.ReasoningEffort,
            supportsThinking);
        return McpExecutionBindingResolution.Success(binding);
    }

    private static bool TryProjectExactCoderTools(IReadOnlyList<AllowedToolDto> resolvedTools, out IReadOnlyList<AllowedToolDto> projectedTools)
    {
        // The shared resolver may append capabilities such as ask_user after applying the saved definition's allowed
        // names. They are irrelevant to inbound Coder execution and never enter its binding. The three expected names
        // themselves remain fail-closed: each must occur exactly once with the required read-only metadata.
        var coderTools = resolvedTools.Where(static tool => CoderToolNames.Contains(tool.Name)).ToArray();
        if (coderTools.Length != CoderToolNames.Count
            || coderTools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != CoderToolNames.Count
            || coderTools.Any(static tool => tool.Category != ToolCategory.ReadLocal
                                             || tool.Location != ToolLocation.ClientLocal
                                             || tool.RequiresApproval))
        {
            projectedTools = [];
            return false;
        }

        var toolsByName = coderTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        projectedTools = Array.AsReadOnly(CoderToolDefinition.Descriptors
                                                             .OrderBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
                                                             .Select(descriptor => new AllowedToolDto
                                                             {
                                                                 Id = toolsByName[descriptor.Name].Id,
                                                                 Name = descriptor.Name,
                                                                 Location = ToolLocation.ClientLocal,
                                                                 Description = descriptor.Description,
                                                                 ParameterSchema = descriptor.ParameterSchema,
                                                                 RequiresApproval = false,
                                                                 Category = ToolCategory.ReadLocal
                                                             })
                                                             .ToArray());
        return true;
    }

    private McpExecutionBinding CreateBinding(string modelId,
        string instructions,
        Guid? agentDefinitionId,
        int? agentDefinitionVersion,
        IReadOnlyList<AllowedToolDto> allowedTools,
        string? reasoningEffort,
        bool supportsThinking)
    {
        IReadOnlyList<AllowedToolDto> immutableAllowedTools = Array.AsReadOnly(allowedTools.ToArray());
        var canonical = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FingerprintVersion);
            writer.WriteString("modelId", modelId);
            writer.WriteString("instructions", instructions);
            if (agentDefinitionId is { } definitionId)
            {
                writer.WriteString("agentDefinitionId", definitionId);
            }
            else
            {
                writer.WriteNull("agentDefinitionId");
            }

            if (agentDefinitionVersion is { } definitionVersion)
            {
                writer.WriteNumber("agentDefinitionVersion", definitionVersion);
            }
            else
            {
                writer.WriteNull("agentDefinitionVersion");
            }

            writer.WriteString("reasoningEffort", reasoningEffort);
            writer.WriteBoolean("supportsThinking", supportsThinking);
            writer.WriteStartArray("tools");
            foreach (var tool in immutableAllowedTools.OrderBy(static tool => tool.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("location", tool.Location.ToString());
                writer.WriteString("category", tool.Category.ToString());
                writer.WriteBoolean("requiresApproval", tool.RequiresApproval);
                writer.WriteString("description", tool.Description);
                writer.WriteString("parameterSchema", tool.ParameterSchema);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var fingerprint = Convert.ToHexString(HMACSHA256.HashData(_nodeKey.Key.Span, canonical.WrittenSpan));
        return new McpExecutionBinding(fingerprint,
            modelId,
            instructions,
            agentDefinitionId,
            agentDefinitionVersion,
            immutableAllowedTools,
            reasoningEffort,
            supportsThinking);
    }

    private async Task<AgentDefinitionRecord?> ResolveDefinitionAsync(string key, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(key, out var id))
        {
            return await _definitionStore.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var definitions = await _definitionStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions.FirstOrDefault(definition => string.Equals(definition.Name, key, StringComparison.Ordinal));
    }

    private static McpExecutionBindingResolution Reject(string failureCode, string displayMessage) =>
        McpExecutionBindingResolution.Rejected(failureCode, displayMessage);
}
