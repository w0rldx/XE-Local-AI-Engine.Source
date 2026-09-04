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
        if (string.IsNullOrWhiteSpace(request.ResolvedSystemPrompt))
        {
            throw new ArgumentException("Resolved system prompt must be provided.", nameof(request));
        }

        if (request.ConversationContext is null)
        {
            throw new ArgumentException("Conversation context must be provided.", nameof(request));
        }

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
            ToolPolicies = request.ToolPolicies is null ? null : new Dictionary<string, object>(request.ToolPolicies, StringComparer.Ordinal),
            ModelProfile = request.ModelProfile,
            ReasoningEffort = ReasoningEffortNormalizer.Normalize(request.ReasoningEffort),
            // Deliberately NOT fed into the config hash below: capable models keep a byte-identical hash, and only the
            // currently-failing incapable models see a (harmless) hash difference.
            SupportsThinking = request.SupportsThinking,
            // Same posture as SupportsThinking above and deliberately NOT hashed: it is derived from the resolved
            // model's chat template, not from the agent's configuration, so the config hash stays byte-identical.
            ReasoningBudgetEnforceable = request.ReasoningBudgetEnforceable,
            // Deliberately NOT fed into the config hash below (mirrors SupportsThinking): sampling is a loopback-only
            // per-send knob, so the no-override path keeps a byte-identical hash and the cross-repo digest stays stable.
            SamplingOptions = request.SamplingOptions,
            // Deliberately NOT fed into the config hash below (mirrors SupportsThinking/SamplingOptions above): the
            // unattended flag is an execution-context bit, not agent configuration, so the SAME agent run on a schedule
            // and run interactively keep a byte-identical hash and the cross-repo digest stays stable.
            IsUnattended = request.IsUnattended,
            // Deliberately NOT fed into the config hash below (same reason as IsUnattended): the relevance filter
            // narrows only the provider-bound tools array, so opting out must leave the digest byte-identical.
            DisableToolRelevanceFilter = request.DisableToolRelevanceFilter,
            RequestedCapabilities = request.RequestedCapabilities is null ? null : [.. request.RequestedCapabilities],
            Timeouts = timeouts,
            OrchestrationSpec = request.OrchestrationSpec,
            // Normalize an empty assigned-skill set to null so the no-skills loopback package carries no skill payload
            // and the config hash below stays byte-identical to the pre-skills digest (the cross-repo round-trip guard).
            Skills = skills,
            // Resolved custom tools ride the package for the session-approval memo only; they are NOT folded into the
            // config hash (their schema/name/approval already ride AllowedTools, which IS hashed). Empty → null so the
            // no-custom-tool package stays byte-identical to before this feature.
            CustomTools = request.CustomTools is { Count: > 0 } resolvedCustomTools ? resolvedCustomTools : null,
            // Per-send decoding constraint (benchmark judge only). Same posture as SamplingOptions above: NOT fed into
            // the config hash, so the null path stays byte-identical and the cross-repo digest is unmoved.
            ResponseJsonSchema = request.ResponseJsonSchema,
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
