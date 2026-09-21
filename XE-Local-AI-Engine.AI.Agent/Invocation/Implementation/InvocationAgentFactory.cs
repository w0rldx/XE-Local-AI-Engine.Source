namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed class InvocationAgentFactory : IInvocationAgentFactory
{
    // The keys with no strongly-typed ChatOptions property live in the shared SamplingOptionKeys because they reach TWO
    // runtimes: OllamaSharp 5.4.25's AbstractionMapper and DeferredLlamaServerChatClient.ApplySamplingPassthrough.

    /// <summary>
    ///     In-process marker on <see cref="ChatOptions.AdditionalProperties" /> that tells the llama.cpp chat client to
    ///     inject <c>chat_template_kwargs.enable_thinking=false</c> into the outbound request.
    /// </summary>
    /// <remarks>
    ///     Set ONLY when reasoning is explicitly OFF on a thinking-capable model, because the llama.cpp OpenAI adapter
    ///     ignores the <c>think:false</c> written alongside it. The key never reaches any wire; only
    ///     <c>DeferredLlamaServerChatClient</c> consumes it, and the literal is intentionally duplicated there because
    ///     the AI.Agent assembly does not reference the LlamaServer provider. Keep the two in sync.
    /// </remarks>
    internal const string LlamaDisableThinkingMarkerKey = "xe.llama.disable_thinking";

    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey" />; kept alongside the disable-thinking marker so both llama.cpp markers read the same here.</summary>
    internal const string LlamaReasoningBudgetMarkerKey = ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey;

    // The MAF skill-discovery tools an agent WITH skills also carries; they reach the model through AIContextProviders,
    // so the factory counts them off ToolRelevanceChatClient's own list to measure the array the send-time hop will.
    private static readonly int MafSkillToolCount = ToolRelevanceChatClient.SkillToolNames.Length;

    private readonly IChatClient _chatClient;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ICustomToolCatalog _customToolCatalog;
    private readonly ILogger<InvocationAgentFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpToolRegistry _mcpToolRegistry;
    private readonly InvocationAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly ToolRelevanceOptions _toolRelevanceOptions;

    public InvocationAgentFactory(IChatClient chatClient,
        IOptions<InvocationAgentOptions> options,
        ILogger<InvocationAgentFactory> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        ICustomToolCatalog customToolCatalog,
        IOptions<ToolRelevanceOptions>? toolRelevanceOptions = null)
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
        _toolRelevanceOptions = toolRelevanceOptions?.Value ?? new ToolRelevanceOptions();
    }

    public async Task<InvocationAgentContext> CreateAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = await ResolveExecutableToolsAsync(definition, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Creating invocation agent context for model {ModelId}.", definition.ModelId);

        var agent = BuildAgent(definition, tools);

        var seedMessages = BuildSeedMessages(definition);

        // Ollama rejects think=true/levels on non-thinking models. Omitting the field preserves reasoning baked into
        // some GGUF templates; think=false suppresses it. Verified with gemma-4-12b-it on Ollama 0.30.5.
        var additionalProperties = new AdditionalPropertiesDictionary();
        if (definition.SupportsThinking)
        {
            // Graded reasoning model: honor the requested effort (false / "low" / "medium" / "high"). minimal/xhigh
            // collapse to think:true here because Ollama 400s on an unknown think level (see ResolveThinkOption).
            var think = ReasoningOptionsResolver.ResolveThinkOption(definition.ReasoningEffort);
            additionalProperties["think"] = think;

            // llama.cpp's OpenAI adapter drops think=false, so explicitly disable thinking through its template marker.
            // The marker is inert on Ollama/Codex and unnecessary for non-thinking models.
            if (think is false)
            {
                additionalProperties[LlamaDisableThinkingMarkerKey] = true;
            }

            // Only send a reasoning budget when llama.cpp can enforce it (a non-empty think-end-tag set); otherwise it
            // silently ignores the field. An explicit sampling budget wins so benchmark replay remains exact.
            var pinnedBudget = definition.Sampling?.ReasoningBudgetTokens is { } pinned && pinned > 0 ? pinned : (int?)null;
            if ((pinnedBudget ?? ReasoningOptionsResolver.ResolveReasoningBudgetTokens(definition.ReasoningEffort)) is { } budgetTokens)
            {
                if (definition.ReasoningBudgetEnforceable)
                {
                    additionalProperties[LlamaReasoningBudgetMarkerKey] = budgetTokens;
                }
                else
                {
                    ReasoningBudgetSkipLog.ReportBudgetSkipped(_logger, definition.ModelId);
                }
            }

            // Codex needs the raw effort to distinguish minimal/xhigh; Ollama ignores this unknown side-channel key.
            var codexEffort = ReasoningOptionsResolver.ResolveCodexReasoningEffort(definition.ReasoningEffort);
            if (codexEffort is not null)
            {
                additionalProperties[ReasoningOptionsResolver.CodexReasoningEffortKey] = codexEffort;
            }

            // Absence preserves an external model's registered default effort.
            var externalEffort = ReasoningOptionsResolver.ResolveExternalReasoningEffort(definition.ModelId, definition.ReasoningEffort);
            if (externalEffort is not null)
            {
                additionalProperties[ReasoningOptionsResolver.ExternalReasoningEffortMarkerKey] = externalEffort;
            }
        }
        else if (ReasoningOptionsResolver.IsReasoningRequested(definition.ReasoningEffort))
        {
            // Omit think: Ollama rejects true/levels, while omission permits chat-template reasoning.
        }
        else
        {
            // Explicitly suppress chat-template reasoning for none/unspecified.
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

        // Constrained decoding. Null (every path but the benchmark judge) leaves ResponseFormat unset, which is what
        // keeps `response_format` off the wire entirely rather than sending a permissive one.
        if (definition.ResponseJsonSchema is { } responseSchema)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(responseSchema);
        }

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
    ///     Builds the turn's <see cref="ChatClientAgent" /> and wraps it with the approval-replay validator.
    /// </summary>
    /// <remarks>
    ///     The inner agent carries NO instructions on either path: they are delivered exactly once per request as the
    ///     leading <see cref="ChatRole.System" /> seed message (<see cref="BuildSeedMessages" />), and MAF would forward
    ///     ctor or options instructions alongside it. Constructor argument order is
    ///     (chatClient, instructions, name, description, tools, loggerFactory, services), verified at
    ///     Microsoft.Agents.AI 1.20.0 and pinned with named arguments. See docs/wiki/04-agent-mode.md ("Building the agent: instructions once, skills through a context provider").
    /// </remarks>
    private AIAgent BuildAgent(InvocationAgentDefinition definition, IList<AITool> tools)
    {
        var agentName = $"{_options.AgentNamePrefix}-{definition.ModelId}";
        const string agentDescription = "XE Local AI Engine worker invocation agent.";

        if (definition.Skills is not { Count: > 0 } skills)
        {
            // No-skills path: instructions are NULL on the agent, carried once by the seed system message. Named
            // arguments pin the ctor order so name/description land as identity.
            return new ApprovalResponseValidatingAgent(new ChatClientAgent(_chatClient,
                instructions: null,
                name: agentName,
                description: agentDescription,
                tools: tools,
                loggerFactory: _loggerFactory,
                services: _serviceProvider));
        }

        // MAAI001: Agent Skills (AgentSkillsProvider/AgentInlineSkill) shipped as [Experimental] in Microsoft.Agents.AI
        // 1.8.0; the scoped suppression stays at the pinned version until there is explicit graduation evidence.
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
                // Instructions are NOT set here — carried once by the seed system message, as on the no-skills path.
                // Only the agent's own tools ride these ChatOptions; RunOptions.ChatOptions carries the per-turn rest.
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
    ///     Builds one MAF <c>AgentInlineSkill</c> from a resolved skill: the full frontmatter constructor plus one
    ///     <c>AddResource</c> per bundled file.
    /// </summary>
    /// <remarks>
    ///     Resources MUST be registered here, before the <c>AgentSkillsProvider</c> is constructed: the provider
    ///     resolves a skill's content once, and a resource added afterwards would exist but never be advertised.
    ///     <c>allowedTools</c> is frontmatter only and grants nothing; scripts are never registered. See
    ///     docs/wiki/04-agent-mode.md ("Building the agent: instructions once, skills through a context provider").
    /// </remarks>
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

    /// <summary>Converts the skill's string metadata map onto the loosely-typed dictionary MAF's frontmatter takes.</summary>
    /// <remarks>Null for an absent or empty map, so a skill without metadata keeps the constructor's own default.</remarks>
    private static AdditionalPropertiesDictionary? ToFrontmatterMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata is { Count: > 0 }
            ? new AdditionalPropertiesDictionary(metadata.Select(static entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)))
            : null;
    }

    /// <summary>
    ///     Applies the developer-gated per-send sampling overrides onto the turn's <see cref="ChatOptions" />.
    /// </summary>
    /// <remarks>
    ///     Native knobs ride the strongly-typed properties; the four without one travel as
    ///     <see cref="AdditionalPropertiesDictionary" /> entries keyed by <see cref="SamplingOptionKeys" />, and reach
    ///     BOTH runtimes. Each field is applied only when set and only when it passes a defensive range guard, so
    ///     NaN, negative or out-of-range keeps the model default. See docs/wiki/04-agent-mode.md ("Per-send sampling
    ///     reaches two runtimes").
    /// </remarks>
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
    ///     Intersects the offer list the definition carries with the executable catalogs, matched by name, and appends
    ///     the tool-relevance escape hatch when the offer is large enough to be filtered.
    /// </summary>
    /// <remarks>
    ///     Offered names come from <c>definition.Tools</c>, which the runner builds from the runtime package's
    ///     allowed-tool list; resolution delegates to the shared <see cref="InvocationToolResolver" /> so the
    ///     single-agent and orchestration factories resolve identically.
    /// </remarks>
    private async Task<IList<AITool>> ResolveExecutableToolsAsync(InvocationAgentDefinition definition, CancellationToken cancellationToken)
    {
        var tools = await InvocationToolResolver.ResolveAsync(definition.Tools,
                                                    _toolRegistry,
                                                    _clientLocalToolRegistry,
                                                    _mcpToolRegistry,
                                                    _customToolCatalog,
                                                    _logger,
                                                    cancellationToken)
                                                .ConfigureAwait(false);

        // The ONLY site in the product that appends the relevance escape hatch — above the pipeline so it is
        // executable, outside the offer so no config hash moves — which is what makes the hop inert elsewhere.
        var skillToolCount = definition.Skills is { Count: > 0 } ? MafSkillToolCount : 0;
        if (ToolRelevanceScope.Current is { Active: true } && tools.Count + skillToolCount > _toolRelevanceOptions.Threshold)
        {
            // The resolver hands back a fixed-size empty array for an empty offer, so materialize before appending.
            var executable = tools as List<AITool> ?? [.. tools];
            executable.Add(new ListToolsFunction(executable));
            return executable;
        }

        return tools;
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
