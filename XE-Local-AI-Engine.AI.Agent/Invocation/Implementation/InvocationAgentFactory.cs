namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed class InvocationAgentFactory : IInvocationAgentFactory
{
    // The option keys for the knobs with no strongly-typed ChatOptions property now live in the shared
    // SamplingOptionKeys (Providers.Abstractions) because they reach TWO runtimes: Ollama reads them from
    // ChatOptions.AdditionalProperties via OllamaSharp 5.4.25's AbstractionMapper (→ OllamaOption.*.Name; verified
    // against the installed assembly 2026-06-05), and DeferredLlamaServerChatClient.ApplySamplingPassthrough patches
    // min_p/repeat_penalty/repeat_last_n (plus the strongly-typed TopK, which the MEAI OpenAI adapter also drops) onto
    // the outbound llama-server body. num_ctx stays Ollama-only — llama-server's window is fixed at launch. The
    // natively-mapped knobs (temperature/top_p/top_k/num_predict/presence_penalty/frequency_penalty/seed/stop) ride the
    // strongly-typed ChatOptions properties.

    /// <summary>
    ///     In-process marker on <see cref="ChatOptions.AdditionalProperties" /> that tells the llama.cpp chat client to
    ///     inject <c>chat_template_kwargs.enable_thinking=false</c> into the outbound request. Set ONLY when
    ///     reasoning is explicitly OFF on a thinking-capable model: the Ollama <c>think:false</c> written alongside it
    ///     suppresses reasoning on the Ollama wire, but the llama.cpp OpenAI adapter ignores <c>think</c>, so a
    ///     Qwen3-class chat template would keep emitting a reasoning block. The key never reaches any wire — Ollama's
    ///     mapper reads a fixed allowlist and Codex reads its own keys, so both stay byte-identical; only
    ///     <c>DeferredLlamaServerChatClient</c> consumes it. The literal is intentionally duplicated there (the AI.Agent
    ///     assembly does not reference the LlamaServer provider); keep the two in sync.
    /// </summary>
    internal const string LlamaDisableThinkingMarkerKey = "xe.llama.disable_thinking";

    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ICustomToolCatalog _customToolCatalog;
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
        IMcpToolRegistry mcpToolRegistry,
        ICustomToolCatalog customToolCatalog)
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
        _customToolCatalog = customToolCatalog ?? throw new ArgumentNullException(nameof(customToolCatalog));
    }

    public async Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = await ResolveExecutableToolsAsync(definition, cancellationToken).ConfigureAwait(false);

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
            var think = ReasoningOptionsResolver.ResolveThinkOption(definition.ReasoningEffort);
            additionalProperties["think"] = think;

            // Reasoning explicitly OFF on a thinking-capable model (ResolveThinkOption returned the bool false, which only
            // happens for "none"): flag the turn so the llama.cpp chat client injects chat_template_kwargs.enable_thinking
            // =false. The think:false above only silences reasoning on the Ollama wire; the llama.cpp OpenAI adapter drops
            // the think key, so without this a Qwen3-class template would still stream a reasoning block. The
            // marker is inert on the Ollama/Codex wires. Non-thinking models take the else branch below and get no marker
            // (there is no default reasoning to disable).
            if (think is false)
            {
                additionalProperties[LlamaDisableThinkingMarkerKey] = true;
            }

            // Codex-only side channel: when a graded/explicit effort is present, also carry the RAW normalized effort
            // string so the Codex Responses boundary can map minimal/xhigh distinctly (the think key above cannot —
            // it would 400 Ollama). Added only for a recognized non-blank effort so the no-effort/blank/on path keeps
            // the single-think dictionary (byte-identical no-override guarantee). Inert on the Ollama wire: the
            // OllamaSharp AbstractionMapper reads only its fixed option allowlist and ignores this unknown key.
            var codexEffort = ReasoningOptionsResolver.ResolveCodexReasoningEffort(definition.ReasoningEffort);
            if (codexEffort is not null)
            {
                additionalProperties[ReasoningOptionsResolver.CodexReasoningEffortKey] = codexEffort;
            }
        }
        else if (ReasoningOptionsResolver.IsReasoningRequested(definition.ReasoningEffort))
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

        // The request budget can never exceed the process that was actually launched. Preserve a smaller explicit
        // num_ctx, but clamp a larger explicit/default budget to the effective process context.
        if (definition.EffectiveContextTokens is { } effectiveContext
            && effectiveContext > 0)
        {
            var requested = effectiveContext;
            if (definition.Sampling?.NumCtx is { } explicitContext && explicitContext > 0)
            {
                requested = explicitContext;
            }

            var clampedContext = Math.Min(requested, effectiveContext);
            additionalProperties[SamplingOptionKeys.NumCtx] = clampedContext;
            if (chatOptions.MaxOutputTokens is { } output)
            {
                chatOptions.MaxOutputTokens = Math.Min(output, clampedContext);
            }
        }

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

        return context;
    }

    /// <summary>
    ///     Builds the turn's <see cref="ChatClientAgent" /> and wraps it with the approval-replay validator. The inner
    ///     agent is built with NO instructions on either path:
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
    ///     Microsoft.Agents.AI 1.15.0; named arguments pin it.
    /// </summary>
    private AIAgent BuildAgent(InvocationAgentDefinition definition, IList<AITool> tools)
    {
        var agentName = $"{_options.AgentNamePrefix}-{definition.ModelId}";
        const string agentDescription = "XE Local AI Engine worker invocation agent.";

        if (definition.Skills is not { Count: > 0 } skills)
        {
            // No-skills path: instructions are NULL on the agent — they are carried once by the seed system message
            // (see BuildSeedMessages). Named arguments pin the 1.15.0 ctor order so name/description land as identity
            // and the model receives the instructions exactly once.
            return new ApprovalResponseValidatingAgent(new ChatClientAgent(_chatClient,
                instructions: null,
                name: agentName,
                description: agentDescription,
                tools: tools,
                loggerFactory: _loggerFactory,
                services: _serviceProvider));
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped as [Experimental] in Microsoft.Agents.AI
        // 1.8.0. The scoped MAAI001 suppression remains at the pinned 1.15.0 until explicit graduation evidence is
        // available. The surface (the full-frontmatter
        // AgentInlineSkill ctor + AgentSkill[] provider ctor) is the documented progressive-disclosure path; the
        // no-skills path above stays on the stable ctor, so the experimental surface is reached only when an agent has
        // assigned skills. Suppress is scoped to this block.
#pragma warning disable MAAI001
        var inlineSkills = new AgentInlineSkill[skills.Count];
        for (var index = 0; index < skills.Count; index++)
        {
            inlineSkills[index] = BuildInlineSkill(skills[index]);
        }

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent below via AIContextProviders; the agent disposes its context providers with itself.
        var skillsProvider = new AgentSkillsProvider(inlineSkills);
#pragma warning restore CA2000
#pragma warning restore MAAI001

        return new ApprovalResponseValidatingAgent(new ChatClientAgent(_chatClient,
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
            _serviceProvider));
    }

    /// <summary>
    ///     Builds one MAF <c>AgentInlineSkill</c> from a resolved skill: the full frontmatter constructor (name,
    ///     description, instructions, license, compatibility, allowed-tools, metadata) plus one <c>AddResource</c> per
    ///     bundled file. The 3-argument call this replaced was the same constructor taking its defaults, so a skill
    ///     carrying no frontmatter and no resources builds byte-identically to before.
    ///     <para>
    ///         Resources MUST be registered here, before the <c>AgentSkillsProvider</c> is constructed: the provider
    ///         resolves a skill's content once and the <c>&lt;available_resources&gt;</c> block is rendered from the
    ///         resources present at that moment. A resource added afterwards would exist but never be advertised, so the
    ///         model would have no way to learn it can be read.
    ///     </para>
    ///     <para>
    ///         <c>allowedTools</c> is carried as frontmatter only. The Agent Skills standard defines it as pre-approval
    ///         rather than restriction, so nothing here grants or withholds a tool on its account — the tool offer and
    ///         the tighten-only approval policy remain the only authorities. Scripts are never registered: an inline
    ///         skill's <c>AddScript</c> takes a delegate, and this node has no execution surface to bind one to.
    ///     </para>
    /// </summary>
    // MAAI001: scoped to the experimental Agent Skills surface, same rationale as the block in BuildAgent that calls this.
#pragma warning disable MAAI001
    internal static AgentInlineSkill BuildInlineSkill(InvocationSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var inlineSkill = new AgentInlineSkill(skill.Name,
            skill.Description,
            skill.Body,
            license: skill.License,
            compatibility: skill.Compatibility,
            allowedTools: skill.AllowedTools,
            metadata: ToFrontmatterMetadata(skill.Metadata));

        if (skill.Resources is { Count: > 0 } resources)
        {
            foreach (var resource in resources)
            {
                inlineSkill.AddResource(resource.Name, resource.Content, resource.Description);
            }
        }

        return inlineSkill;
    }
#pragma warning restore MAAI001

    /// <summary>
    ///     Converts the skill's string metadata map onto the loosely-typed dictionary MAF's frontmatter takes. Null for
    ///     an absent or empty map so a skill without metadata keeps the constructor's own default.
    /// </summary>
    private static AdditionalPropertiesDictionary? ToFrontmatterMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata is { Count: > 0 }
            ? new AdditionalPropertiesDictionary(metadata.Select(static entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)))
            : null;
    }

    /// <summary>
    ///     Applies the developer-gated per-send sampling overrides onto the turn's <see cref="ChatOptions" />. Native
    ///     knobs ride the strongly-typed properties; the four knobs without a native property travel as
    ///     <see cref="AdditionalPropertiesDictionary" /> entries keyed by <see cref="SamplingOptionKeys" /> (the same
    ///     channel the existing <c>think</c> property proves). Each field is applied only when set and only when it
    ///     passes a defensive range guard (NaN/negative/out-of-range → skipped). When both <c>MaxOutputTokens</c> and
    ///     <c>NumCtx</c> are set, the output cap is clamped to the context window.
    ///     <para>
    ///         These entries reach BOTH runtimes: OllamaSharp's <c>AbstractionMapper</c> maps all four onto the Ollama
    ///         wire, and <c>DeferredLlamaServerChatClient.ApplySamplingPassthrough</c> patches
    ///         <c>min_p</c>/<c>repeat_penalty</c>/<c>repeat_last_n</c> — plus the strongly-typed <c>TopK</c>, which the
    ///         MEAI OpenAI adapter drops — onto the llama-server body. <c>num_ctx</c> is deliberately NOT sent to
    ///         llama-server (its window is fixed at process launch); there it only budgets client-side history.
    ///     </para>
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
            additionalProperties[SamplingOptionKeys.NumCtx] = resolvedCtx;
        }

        if (sampling.MaxOutputTokens is { } maxOutputTokens && maxOutputTokens > 0)
        {
            // Safety clamp: an output budget larger than the context window would be rejected/truncated by the model.
            var clamped = numCtx is { } window ? Math.Min(maxOutputTokens, window) : maxOutputTokens;
            chatOptions.MaxOutputTokens = clamped;
        }

        if (IsValidUnitFloat(sampling.MinP))
        {
            additionalProperties[SamplingOptionKeys.MinP] = sampling.MinP!.Value;
        }

        if (sampling.RepeatPenalty is { } repeatPenalty && !float.IsNaN(repeatPenalty) && repeatPenalty >= 0f)
        {
            additionalProperties[SamplingOptionKeys.RepeatPenalty] = repeatPenalty;
        }

        if (sampling.RepeatLastN is { } repeatLastN && repeatLastN >= -1)
        {
            additionalProperties[SamplingOptionKeys.RepeatLastN] = repeatLastN;
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
    private Task<IList<AITool>> ResolveExecutableToolsAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken)
    {
        return InvocationToolResolver.ResolveAsync(definition.Tools,
            _toolRegistry,
            _clientLocalToolRegistry,
            _mcpToolRegistry,
            _customToolCatalog,
            _logger,
            cancellationToken);
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
