namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;

internal sealed class InvocationAgentFactory : IInvocationAgentFactory
{
    /// <summary>
    ///     The binary reasoning-"on" sentinel for a model that lacks the Ollama <c>thinking</c> capability but reasons
    ///     by default. "on" — and any graded level (low/medium/high) carried onto such a model — makes the factory OMIT
    ///     the think field so the model's built-in reasoning runs; only "none"/unspecified suppresses it via think:false
    ///     (see <see cref="IsReasoningRequested" />). Thinking-capable models never take this path — they honor
    ///     false/low/medium/high via <see cref="ResolveThinkOption" />.
    /// </summary>
    private const string BinaryReasoningOn = "on";

    // Ollama option keys read from ChatOptions.AdditionalProperties by OllamaSharp 5.4.25
    // (OllamaSharp.MicrosoftAi.AbstractionMapper → OllamaOption.*.Name; verified against the installed assembly
    // 2026-06-05). The natively-mapped knobs (temperature/top_p/top_k/num_predict/presence_penalty/frequency_penalty/
    // seed/stop) ride the strongly-typed ChatOptions properties instead and so are not listed here.
    private const string OllamaMinPKey = "min_p";
    private const string OllamaRepeatPenaltyKey = "repeat_penalty";
    private const string OllamaRepeatLastNKey = "repeat_last_n";
    private const string OllamaNumCtxKey = "num_ctx";

    /// <summary>
    ///     Codex-only side channel carrying the RAW normalized reasoning-effort string
    ///     (minimal/low/medium/high/xhigh) for a thinking-capable model, so the Codex Responses boundary can map
    ///     it to <c>ResponseReasoningEffortLevel</c> with full fidelity. The Ollama <c>think</c> key cannot carry
    ///     <c>minimal</c>/<c>xhigh</c> (Ollama 400s on an unknown think level), so those collapse to
    ///     <c>think:true</c> there; this key preserves the distinction without affecting the Ollama wire — the
    ///     OllamaSharp AbstractionMapper reads only its fixed option allowlist and ignores unknown keys. The key
    ///     is added ONLY when a graded/explicit effort is present, so the no-effort path stays byte-identical
    ///     (single <c>think</c> entry).
    /// </summary>
    internal const string CodexReasoningEffortKey = "codex_reasoning_effort";

    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ILogger<InvocationAgentFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly InvocationAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentToolRegistry _toolRegistry;

    public InvocationAgentFactory(IChatClient chatClient,
        IOptions<InvocationAgentOptions> options,
        ILogger<InvocationAgentFactory> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options.Value;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _clientLocalToolRegistry = clientLocalToolRegistry ?? throw new ArgumentNullException(nameof(clientLocalToolRegistry));
        _mcpToolRegistry = mcpToolRegistry ?? throw new ArgumentNullException(nameof(mcpToolRegistry));
    }

    public Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = ResolveExecutableTools(definition);

        _logger.LogDebug("Creating invocation agent context for model {ModelId}.", definition.ModelId);

        var agent = BuildAgent(definition, tools);

        var seedMessages = BuildSeedMessages(definition);

        // Ollama think option, by model capability:
        //  - Thinking-capable model: honor the requested effort (false, low, medium, or high).
        //  - Non-thinking model: omit the think field when reasoning is requested (the binary on, OR a graded level
        //    carried onto it by an agent definition or a stale composer selection), and send think false only for
        //    none or unspecified. Sending think true or a level is rejected by Ollama with HTTP 400 (does not support
        //    thinking); think false is accepted but actively suppresses the reasoning some GGUF chat templates (e.g.
        //    unsloth gemma-4-12b-it) emit by default, whereas omitting the field lets that template reasoning through
        //    so the user sees a reasoning block. Verified live against gemma-4-12b-it on Ollama 0.30.5: think false
        //    returns empty thinking, while omitting the field returns populated thinking. Cloud providers default
        //    SupportsThinking to true and ignore the unknown property, so their option dictionary is unchanged.
        var additionalProperties = new AdditionalPropertiesDictionary();
        if (definition.SupportsThinking)
        {
            // Graded reasoning model: honor the requested effort (false / "low" / "medium" / "high"). minimal/xhigh
            // collapse to think:true here because Ollama 400s on an unknown think level (see ResolveThinkOption).
            additionalProperties["think"] = ResolveThinkOption(definition.ReasoningEffort);

            // Codex-only side channel: when a graded/explicit effort is present, also carry the RAW normalized effort
            // string so the Codex Responses boundary can map minimal/xhigh distinctly (the think key above cannot —
            // it would 400 Ollama). Added only for a recognized non-blank effort so the no-effort/blank/on path keeps
            // the single-think dictionary (byte-identical no-override guarantee). Inert on the Ollama wire: the
            // OllamaSharp AbstractionMapper reads only its fixed option allowlist and ignores this unknown key.
            var codexEffort = ResolveCodexReasoningEffort(definition.ReasoningEffort);
            if (codexEffort is not null)
            {
                additionalProperties[CodexReasoningEffortKey] = codexEffort;
            }
        }
        else if (IsReasoningRequested(definition.ReasoningEffort))
        {
            // Non-thinking model, reasoning requested (binary "on" OR a graded low/medium/high carried onto a model
            // that cannot do graded thinking): OMIT the think field entirely so the model's default (chat-template-
            // baked) reasoning is allowed through. We must NOT send think:true or a level — Ollama returns HTTP 400
            // ("does not support thinking") for a model without the thinking capability; only omission lets the
            // built-in reasoning run. The key is therefore intentionally left out here.
        }
        else
        {
            // Non-thinking model, reasoning OFF ("none") or unspecified (the safe default for agent/legacy paths):
            // think:false actively suppresses the reasoning some GGUF templates emit by default.
            additionalProperties["think"] = false;
        }

        var chatOptions = new ChatOptions
        {
            ModelId = definition.ModelId,
            AdditionalProperties = additionalProperties
        };

        // Developer-gated per-send sampling overrides. Null (the default, mode-off) leaves chatOptions byte-identical to
        // the pre-sampling path — the no-override guarantee.
        ApplySamplingOptions(chatOptions, additionalProperties, definition.Sampling);

        InvocationAgentContext context = new()
        {
            Agent = agent,
            Session = null,
            SeedMessages = seedMessages,
            RunOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = chatOptions
            }
        };

        context.Items["modelId"] = definition.ModelId;
        context.Items["toolsEnabled"] = tools.Count > 0;

        return Task.FromResult(context);
    }

    /// <summary>
    ///     Builds the turn's <see cref="ChatClientAgent" />. The agent is built with NO instructions on either path:
    ///     the system instructions are delivered exactly once per request as the leading <see cref="ChatRole.System" />
    ///     seed message (<see cref="BuildSeedMessages" />, replayed by the invocation runner). Passing them to the
    ///     ctor's <c>instructions</c> parameter (or to <see cref="ChatOptions.Instructions" />) as well would
    ///     double-send them — MAF forwards ctor/options instructions to the <see cref="IChatClient" /> on every
    ///     invocation, alongside the seed system message. The agent's <c>name</c>/<c>description</c> carry identity
    ///     only (not sent to the model as content). With NO resolved skills this uses the 7-arg constructor; with one
    ///     or more resolved skills it builds a MAF <see cref="AgentSkillsProvider" /> from <see cref="AgentInlineSkill" />
    ///     records (name + description + body-as-instructions; no scripts/resources in v1) and constructs the agent
    ///     through the <see cref="ChatClientAgentOptions" /> constructor with that provider attached via
    ///     <see cref="ChatClientAgentOptions.AIContextProviders" />. The provider serves each skill's body on demand
    ///     (progressive disclosure); its skill-discovery tools are serviced by the same FunctionInvokingChatClient that
    ///     already services the agent's own tools. Constructor argument order is
    ///     (chatClient, instructions, name, description, tools, loggerFactory, services) — verified against
    ///     Microsoft.Agents.AI 1.13.0; named arguments pin it.
    /// </summary>
    private ChatClientAgent BuildAgent(InvocationAgentDefinition definition, IList<AITool> tools)
    {
        var agentName = $"{_options.AgentNamePrefix}-{definition.ModelId}";
        const string agentDescription = "XE Local AI Engine worker invocation agent.";

        if (definition.Skills is not { Count: > 0 } skills)
        {
            // No-skills path: instructions are NULL on the agent — they are carried once by the seed system message
            // (see BuildSeedMessages). Named arguments pin the 1.13.0 ctor order so name/description land as identity
            // and the model receives the instructions exactly once.
            return new ChatClientAgent(_chatClient,
                instructions: null,
                name: agentName,
                description: agentDescription,
                tools: tools,
                loggerFactory: _loggerFactory,
                services: _serviceProvider);
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped as [Experimental] in Microsoft.Agents.AI
        // 1.8.0 (verified against the 1.8.0 .xml; pinned version is now 1.13.0, not re-verified). The surface (3-arg
        // AgentInlineSkill + AgentSkill[] provider ctor) is the documented progressive-disclosure path; the no-skills
        // path above stays on the stable ctor, so the experimental surface is reached only when an agent has assigned
        // skills. Suppress is scoped to this block.
#pragma warning disable MAAI001
        var inlineSkills = new AgentInlineSkill[skills.Count];
        for (var index = 0; index < skills.Count; index++)
        {
            var skill = skills[index];
            inlineSkills[index] = new AgentInlineSkill(skill.Name, skill.Description, skill.Body);
        }

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent below via AIContextProviders; the agent disposes its context providers with itself.
        var skillsProvider = new AgentSkillsProvider(inlineSkills);
#pragma warning restore CA2000
#pragma warning restore MAAI001

        return new ChatClientAgent(_chatClient,
            new ChatClientAgentOptions
            {
                Name = agentName,
                Description = agentDescription,
                // Instructions are NOT set here (Instructions null) — they are carried once by the seed system message,
                // exactly as on the no-skills path, so the two paths deliver instructions identically. Only the agent's
                // own tools ride these ChatOptions; the per-turn RunOptions.ChatOptions still carries model id / think /
                // sampling.
                ChatOptions = new ChatOptions
                {
                    Tools = tools
                },
                AIContextProviders = [skillsProvider]
            },
            _loggerFactory,
            _serviceProvider);
    }

    /// <summary>
    ///     Applies the developer-gated per-send sampling overrides onto the turn's <see cref="ChatOptions" />. Native
    ///     knobs ride the strongly-typed properties (mapped by OllamaSharp's <c>AbstractionMapper</c>); the four Ollama
    ///     knobs without a native property travel as <see cref="AdditionalPropertiesDictionary" /> entries keyed by the
    ///     raw Ollama option name (the same channel the existing <c>think</c> property proves). Each field is applied only
    ///     when set and only when it passes a defensive range guard (NaN/negative/out-of-range → skipped). When both
    ///     <c>MaxOutputTokens</c> and <c>NumCtx</c> are set, the output cap is clamped to the context window.
    /// </summary>
    private static void ApplySamplingOptions(ChatOptions chatOptions,
        AdditionalPropertiesDictionary additionalProperties,
        InvocationSamplingOptions? sampling)
    {
        if (sampling is null)
        {
            return;
        }

        // Temperature is accepted in [0, 2] (the UI cap); out-of-range → skip and keep the model default.
        if (IsValidRangedFloat(sampling.Temperature, min: 0f, max: 2f))
        {
            chatOptions.Temperature = sampling.Temperature;
        }

        if (IsValidUnitFloat(sampling.TopP))
        {
            chatOptions.TopP = sampling.TopP;
        }

        if (sampling.TopK is { } topK && topK > 0)
        {
            chatOptions.TopK = topK;
        }

        // Penalties are accepted in [-2, 2] (the UI cap); out-of-range → skip and keep the model default.
        if (IsValidRangedFloat(sampling.PresencePenalty, min: -2f, max: 2f))
        {
            chatOptions.PresencePenalty = sampling.PresencePenalty;
        }

        if (IsValidRangedFloat(sampling.FrequencyPenalty, min: -2f, max: 2f))
        {
            chatOptions.FrequencyPenalty = sampling.FrequencyPenalty;
        }

        // Seed floor mirrors the UI: -1 is Ollama's "random seed" sentinel, anything below is invalid → skip.
        if (sampling.Seed is { } seed && seed >= -1)
        {
            chatOptions.Seed = seed;
        }

        if (sampling.Stop is { Count: > 0 } stop)
        {
            var sequences = stop.Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (sequences.Count > 0)
            {
                chatOptions.StopSequences = sequences;
            }
        }

        // num_ctx is read first so the output cap below can be clamped to a valid context window.
        var numCtx = sampling.NumCtx is { } ctx && ctx > 0 ? ctx : (int?)null;
        if (numCtx is { } resolvedCtx)
        {
            additionalProperties[OllamaNumCtxKey] = resolvedCtx;
        }

        if (sampling.MaxOutputTokens is { } maxOutputTokens && maxOutputTokens > 0)
        {
            // Safety clamp: an output budget larger than the context window would be rejected/truncated by the model.
            var clamped = numCtx is { } window ? Math.Min(maxOutputTokens, window) : maxOutputTokens;
            chatOptions.MaxOutputTokens = clamped;
        }

        if (IsValidUnitFloat(sampling.MinP))
        {
            additionalProperties[OllamaMinPKey] = sampling.MinP!.Value;
        }

        if (sampling.RepeatPenalty is { } repeatPenalty && !float.IsNaN(repeatPenalty) && repeatPenalty >= 0f)
        {
            additionalProperties[OllamaRepeatPenaltyKey] = repeatPenalty;
        }

        if (sampling.RepeatLastN is { } repeatLastN && repeatLastN >= -1)
        {
            additionalProperties[OllamaRepeatLastNKey] = repeatLastN;
        }
    }

    private static bool IsValidRangedFloat(float? value, float min, float max)
    {
        return value is { } resolved && !float.IsNaN(resolved) && resolved >= min && resolved <= max;
    }

    private static bool IsValidUnitFloat(float? value)
    {
        return IsValidRangedFloat(value, min: 0f, max: 1f);
    }

    /// <summary>
    ///     Intersects the offer list the definition carries with the executable catalogs, matched by name (Option A
    ///     built-in / B ClientLocal / C MCP). Offered names are sourced from <c>definition.Tools</c> (which the runner
    ///     builds from the runtime package's allowed-tool list). Delegates to the shared
    ///     <see cref="InvocationToolResolver" /> so the single-agent and orchestration factories resolve tools
    ///     identically.
    /// </summary>
    private IList<AITool> ResolveExecutableTools(InvocationAgentDefinition definition)
    {
        return InvocationToolResolver.Resolve(definition.Tools, _toolRegistry, _clientLocalToolRegistry, _mcpToolRegistry, _logger);
    }

    private static object ResolveThinkOption(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return true;
        }

        var normalized = reasoningEffort.Trim();
        if (string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        if (string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        // minimal / xhigh are Codex (OpenAI Responses) reasoning levels that Ollama does NOT understand — sending
        // think:"minimal"/"xhigh" returns HTTP 400 ("unknown think level"). They are only offered for Codex models in
        // the composer, but should an agent definition pin one (or a stale composer selection carry one onto an Ollama
        // thinking model) we map them to think:true (reason) so the Ollama path stays safe. The Codex boundary reads
        // the un-collapsed level from the CodexReasoningEffortKey side channel instead.
        // Default to think:true (reason). This also intentionally covers the "on" binary-reasoning sentinel
        // (<see cref="BinaryReasoningOn" />): it is normally handled in the !SupportsThinking branch, and only reaches
        // this graded path defensively (the React clamp keeps "on" off thinking-capable models) — where "reason" is the
        // right meaning for a model that can think.
        return true;
    }

    /// <summary>
    ///     Returns the canonical reasoning effort to carry on the Codex-only <see cref="CodexReasoningEffortKey" />
    ///     side channel, or <see langword="null" /> to omit it. Recognizes the OpenAI Responses graded levels
    ///     (<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>) and explicit <c>none</c>; blank, the
    ///     binary <c>on</c> sentinel, and any unrecognized value return <see langword="null" /> so the Codex boundary
    ///     falls back to interpreting the Ollama <c>think</c> value (true → its default effort). The input is expected
    ///     already normalized upstream by the Application layer's reasoning-effort normalizer.
    /// </summary>
    private static string? ResolveCodexReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "NONE" => "none",
            "MINIMAL" => "minimal",
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
            "XHIGH" => "xhigh",
            _ => null
        };
    }

    /// <summary>
    ///     True when the effort asks the model to reason: the binary <see cref="BinaryReasoningOn" /> sentinel or a
    ///     graded level (low/medium/high). Used ONLY on the non-thinking-model branch — a graded level can be carried
    ///     onto a model that lacks the Ollama <c>thinking</c> capability (an agent definition pins it, or the composer
    ///     keeps a stale selection across a model switch). The model cannot honor the graded level (Ollama 400s on
    ///     <c>think:&lt;level&gt;</c>), but the user still asked to reason, so the caller OMITS the think field and lets
    ///     the model's built-in reasoning run. Only <c>none</c> (or unspecified/blank) returns false → think:false.
    /// </summary>
    private static bool IsReasoningRequested(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return false;
        }

        var normalized = reasoningEffort.Trim();
        return string.Equals(normalized, BinaryReasoningOn, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ChatMessage> BuildSeedMessages(InvocationAgentDefinition definition)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, definition.Instructions)
        ];

        messages.AddRange(definition.ConversationContext);

        return messages;
    }
}
