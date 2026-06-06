namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

/// <summary>
///     Represents local chat runtime package builder.
/// </summary>
public sealed class LocalChatRuntimePackageBuilder : ILocalChatRuntimePackageBuilder
{
    public RuntimePackage Build(LocalChatRuntimePackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResolvedSystemPrompt);
        ArgumentNullException.ThrowIfNull(request.ConversationContext);

        List<AllowedToolDto> allowedTools = request.AllowedTools is null ? [] : [.. request.AllowedTools];
        var timeouts = request.Timeouts ?? new TimeoutSettings();
        // Collapse a null/empty assigned-skill set to null so the no-skills path carries no skill payload and hashes
        // byte-identically to the pre-skills digest (the config-hash payload omits skills entirely when null).
        var skills = request.Skills is { Count: > 0 } resolvedSkills ? resolvedSkills : null;

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
            ReasoningEffort = ReasoningEffortNormalizer.Normalize(request.ReasoningEffort),
            // Deliberately NOT fed into the config hash below: capable models keep a byte-identical hash, and only the
            // currently-failing incapable models see a (harmless) hash difference.
            SupportsThinking = request.SupportsThinking,
            // Deliberately NOT fed into the config hash below (mirrors SupportsThinking): sampling is a loopback-only
            // per-send knob, so the no-override path keeps a byte-identical hash and the cross-repo digest stays stable.
            SamplingOptions = request.SamplingOptions,
            RequestedCapabilities = request.RequestedCapabilities is null ? null : [.. request.RequestedCapabilities],
            Timeouts = timeouts,
            OrchestrationSpec = request.OrchestrationSpec,
            // Normalize an empty assigned-skill set to null so the no-skills loopback package carries no skill payload
            // and the config hash below stays byte-identical to the pre-skills digest (the cross-repo round-trip guard).
            Skills = skills,
            // UNLIKE SupportsThinking/Sampling above, the resolved skill set IS fed into the config hash: skill bodies
            // ride MAF progressive disclosure (NOT in ResolvedSystemPrompt), so a body edit/rename/picklist change would
            // not move the prompt — folding the set (body HASHED, WhenWritingNull) is what invalidates resume.
            ConfigHash = RuntimePackageConfigHash.Compute(request.AgentDefinitionVersion,
                request.ResolvedSystemPrompt,
                MapAllowedTools(allowedTools),
                request.ModelProfile,
                timeouts,
                request.ReasoningEffort,
                request.OrchestrationSpec,
                skills)
        };
    }

    private static IReadOnlyList<MixedEnvelopeAllowedToolDto> MapAllowedTools(IReadOnlyList<AllowedToolDto> allowedTools)
    {
        return
        [
            .. allowedTools.Select(static tool => new MixedEnvelopeAllowedToolDto
            {
                Name = tool.Name,
                Description = null,
                Schema = tool.ParameterSchema,
                Location = tool.Location,
                RequiresApproval = tool.RequiresApproval
            })
        ];
    }
}
