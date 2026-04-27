namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

public sealed class LocalChatRuntimePackageBuilder : ILocalChatRuntimePackageBuilder
{
    public RuntimePackage Build(LocalChatRuntimePackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResolvedSystemPrompt);
        ArgumentNullException.ThrowIfNull(request.ConversationContext);

        List<AllowedToolDto> allowedTools = request.AllowedTools is null ? [] : [.. request.AllowedTools];
        var timeouts = request.Timeouts ?? new TimeoutSettings();

        return new RuntimePackage
        {
            InvocationId = request.InvocationId,
            ConversationId = request.ConversationId,
            ClientNodeId = request.ClientNodeId ?? LocalChatLoopbackDefaults.ClientNodeId,
            AgentDefinitionVersion = request.AgentDefinitionVersion,
            ResolvedSystemPrompt = request.ResolvedSystemPrompt,
            ConversationContext = [.. request.ConversationContext.OrderBy(static message => message.SortOrder)],
            AllowedTools = allowedTools,
            ToolPolicies = request.ToolPolicies is null ? null : new Dictionary<string, object>(request.ToolPolicies),
            ModelProfile = request.ModelProfile,
            ReasoningEffort = NormalizeReasoningEffort(request.ReasoningEffort),
            RequestedCapabilities = request.RequestedCapabilities is null ? null : [.. request.RequestedCapabilities],
            Timeouts = timeouts,
            ConfigHash = RuntimePackageConfigHash.Compute(request.AgentDefinitionVersion,
                request.ResolvedSystemPrompt,
                MapAllowedTools(allowedTools),
                request.ModelProfile,
                timeouts,
                request.ReasoningEffort)
        };
    }

    private static string? NormalizeReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var normalized = reasoningEffort.Trim();
        if (string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        if (string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase) ? "high" : null;
    }

    private static IReadOnlyList<MixedEnvelopeAllowedToolDto> MapAllowedTools(IReadOnlyList<AllowedToolDto> allowedTools)
    {
        return
        [
            .. allowedTools.Select(static tool => new MixedEnvelopeAllowedToolDto
            {
                Name = tool.Name,
                Description = null,
                Schema = tool.ParameterSchema
            })
        ];
    }
}
