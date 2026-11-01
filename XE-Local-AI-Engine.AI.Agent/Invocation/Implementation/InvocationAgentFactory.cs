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
            if ((pinnedBudget ?? ReasoningOptionsResolver.ResolveReasoningBudgetTokens(definition.ReasoningEffort, definition.ReasoningBudgets)) is { } budgetTokens)
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
        InvocationSamplingRules.Apply(chatOptions, additionalProperties, definition.Sampling);

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

        ReasoningOptionsResolver.ApplyDefaultOutputCap(chatOptions, additionalProperties, definition.DefaultMaxOutputTokens);

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
    ///     Instructions live only in <see cref="BuildSeedMessages" />, unless explicitly omitted. Supplying them to
    ///     the inner agent would duplicate the system prompt. See docs/wiki/04-agent-mode.md ("Building the agent:
    ///     instructions once, skills through a context provider").
    /// </remarks>
    private AIAgent BuildAgent(InvocationAgentDefinition definition, IList<AITool> tools)
    {
        var agentName = $"{_options.AgentNamePrefix}-{definition.ModelId}";
        const string agentDescription = "XE Local AI Engine worker invocation agent.";

        if (definition.Skills is not { Count: > 0 } skills)
        {
            // No-skills path: instructions are NULL on the agent, carried once by the seed system message.
            return BuildApprovalValidatedAgent(_chatClient, agentName, agentDescription, tools, _loggerFactory, _serviceProvider);
        }

#pragma warning disable CA2000 // Ownership transfers to the ChatClientAgent below via AIContextProviders; the agent disposes its context providers with itself.
        var skillsProvider = InvocationSkillsProvider.Create(skills);
#pragma warning restore CA2000

        return BuildApprovalValidatedAgent(_chatClient, agentName, agentDescription, tools, _loggerFactory, _serviceProvider,
            skillsProvider: skillsProvider);
    }

    /// <summary>
    ///     The one construction of an approval-validated <see cref="ChatClientAgent" />; tests build through it so they
    ///     cannot drift from production.
    /// </summary>
    /// <remarks>
    ///     <see cref="ChatClientAgentOptions.DisableApprovalResponseBinding" /> is set on purpose: since
    ///     Microsoft.Agents.AI 1.22 the default binding client trusts only approval requests recorded in an
    ///     <see cref="AgentSession" />, and invocation runs are sessionless, so every threadless resume would fail.
    ///     <see cref="ApprovalResponseValidatingAgent" /> enforces the binding (and exactly-once) instead.
    /// </remarks>
    internal static AIAgent BuildApprovalValidatedAgent(IChatClient chatClient,
        string name,
        string description,
        IList<AITool> tools,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        string? instructions = null,
        AIContextProvider? skillsProvider = null)
    {
        return new ApprovalResponseValidatingAgent(new ChatClientAgent(chatClient,
            new ChatClientAgentOptions
            {
                Name = name,
                Description = description,
                // Only the agent's own tools (and a test's instructions) ride these ChatOptions; RunOptions.ChatOptions
                // carries the per-turn rest.
                ChatOptions = new ChatOptions
                {
                    Tools = tools,
                    Instructions = instructions
                },
                AIContextProviders = skillsProvider is null ? null : [skillsProvider],
                DisableApprovalResponseBinding = true
            },
            loggerFactory,
            services));
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
        var tools = await InvocationToolResolver.ResolveForAgentTurnAsync(definition.Tools,
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
        if (ToolRelevanceScope.Current is { Active: true } scope && (scope.FittedOffer is not null || tools.Count + skillToolCount > _toolRelevanceOptions.Threshold))
        {
            // The resolver hands back a fixed-size empty array for an empty offer, so materialize before appending.
            var executable = tools as List<AITool> ?? [.. tools];
            executable.Add(new ListToolsFunction(executable));
            return executable;
        }

        return tools;
    }

    /// <summary>The turn's opening message list: the instructions as the leading System message unless omitted, then the conversation.</summary>
    /// <remarks>Internal for the runner's pre-launch budget (<c>InvocationRunner.BudgetFirstRound</c>), which must measure this exact shape.</remarks>
    internal static IReadOnlyList<ChatMessage> BuildSeedMessages(InvocationAgentDefinition definition)
    {
        List<ChatMessage> messages = definition.OmitSystemPrompt
            ? []
            : [new ChatMessage(ChatRole.System, definition.Instructions)];

        messages.AddRange(definition.ConversationContext);

        return messages;
    }
}
