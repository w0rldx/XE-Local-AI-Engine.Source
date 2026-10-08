namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Default <see cref="IChatContextEstimateService" />: the send path's model head, turn resolution, tool offer and
///     fallback prompt, measured with the same estimator and framing the outer context budgeter charges.
/// </summary>
internal sealed class ChatContextEstimateService : IChatContextEstimateService
{
    private readonly ConversationContextBudgetOptions _budgetOptions;
    private readonly IDefaultAgentProvider _defaultAgentProvider;
    private readonly ITokenEstimator _estimator;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly ILocalModelDetailsResolver _modelDetailsResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;
    private readonly ChatTurnResolver _turnResolver;

    public ChatContextEstimateService(ChatTurnResolver turnResolver,
        IDefaultAgentProvider defaultAgentProvider,
        INodeSettingsStore nodeSettingsStore,
        ILocalDefaultChatModelResolver localDefaultChatModelResolver,
        INodeRuntimeSettings runtimeSettings,
        ILocalToolOfferProvider localToolOfferProvider,
        IToolApprovalPolicy toolApprovalPolicy,
        IOptions<LocalChatAgentOptions> localChatOptions,
        ILocalModelDetailsResolver modelDetailsResolver,
        ITokenEstimator estimator,
        IOptions<ConversationContextBudgetOptions> budgetOptions)
    {
        ArgumentNullException.ThrowIfNull(budgetOptions);
        _turnResolver = turnResolver ?? throw new ArgumentNullException(nameof(turnResolver));
        _defaultAgentProvider = defaultAgentProvider ?? throw new ArgumentNullException(nameof(defaultAgentProvider));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _localDefaultChatModelResolver = localDefaultChatModelResolver ?? throw new ArgumentNullException(nameof(localDefaultChatModelResolver));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _toolApprovalPolicy = toolApprovalPolicy ?? throw new ArgumentNullException(nameof(toolApprovalPolicy));
        _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
        _modelDetailsResolver = modelDetailsResolver ?? throw new ArgumentNullException(nameof(modelDetailsResolver));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _budgetOptions = budgetOptions.Value;
    }

    public async Task<NodeChatContextWindowDto?> EstimateAsync(ChatContextEstimateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputs = await ResolveAsync(request.ModelName, request.AgentId, request.UseLocalTools, cancellationToken);
        var resolution = inputs.Resolution;
        var systemPrompt = inputs.SystemPrompt;
        var offer = inputs.Offer;
        if (resolution.EffectiveModel is not { } model)
        {
            return null;
        }

        var window = await ResolveWindowTokensAsync(model, resolution.EffectiveModelIsCloud, request.NumCtx, cancellationToken);
        if (window is not { } windowTokens)
        {
            return null;
        }

        var divisor = _estimator.ResolveDivisor(model);
        var systemPromptTokens = ConversationContextBudgeter.EstimateSystemPromptTokens(_estimator, systemPrompt, divisor);

        // Same rendering and per-tool charge the outer budgeter applies to the package's offer, so the estimate and the
        // first round's fixed overhead agree; the preamble is charged once when any tool is offered, as there.
        var allowedTools = offer.AllowedTools ?? [];
        var definitions = InvocationRunner.BuildToolBudgetDefinitions(allowedTools);
        var tools = new List<NodeChatContextWindowTool>(Math.Min(definitions.Count, NodeChatContextWindowDto.MaxToolEntries));
        var toolSchemaTokens = 0;
        for (var i = 0; i < definitions.Count; i++)
        {
            var tokens = ConversationContextBudgeter.EstimateToolDefinitionTokens(_estimator, definitions[i], divisor);
            toolSchemaTokens += tokens;
            if (tools.Count < NodeChatContextWindowDto.MaxToolEntries)
            {
                tools.Add(new NodeChatContextWindowTool { Name = allowedTools[i].Name, Tokens = tokens });
            }
        }

        var preambleTokens = definitions.Count > 0 ? _estimator.ResolveToolTemplatePreamble(model) : 0;

        // The send path's reserve: the node floor, widened by a positive per-send max output, never more than the window,
        // as TurnPolicy.WithEffectiveContext clamps it; usable is the margins-adjusted window minus the reserve.
        var reserved = Math.Min(TurnPolicy.ResolveReservedOutputTokens(reservedOutputTokensOverride: null, request.MaxOutputTokens, _budgetOptions.ReservedOutputTokenFloor),
            windowTokens);
        var usable = Math.Max(TokenEstimatorCalibrationStore.ApplyEstimateMargins(windowTokens, _estimator.ResolveObservedCorrection(model)) - reserved, 0);

        return new NodeChatContextWindowDto
        {
            Kind = NodeChatContextWindowKind.PreSendEstimate,
            ModelId = model,
            WindowTokens = windowTokens,
            ReservedOutputTokens = reserved,
            UsableWindowTokens = usable,
            SafetyMarginTokens = Math.Max(windowTokens - reserved - usable, 0),
            Estimated = new NodeChatContextWindowEstimate
            {
                SystemPromptTokens = systemPromptTokens,
                // The skill listing is added at the agent boundary during the run; it is unknown pre-send.
                InstructionsTokens = 0,
                ToolSchemaTokens = toolSchemaTokens,
                ToolTemplatePreambleTokens = preambleTokens,
                KnowledgeTokens = 0,
                AttachmentTokens = 0,
                CompactionTokens = 0,
                ConversationTokens = 0,
                TotalTokens = systemPromptTokens + toolSchemaTokens + preambleTokens
            },
            Tools = tools
        };
    }

    /// <summary>
    ///     The send path's resolution for a fresh turn: the resolved turn, the system prompt it would send and its tool
    ///     offer. Internal so a parity test can hold it against what <see cref="NodeChatStreamService" /> hands the runner.
    /// </summary>
    internal async Task<ResolvedEstimateInputs> ResolveAsync(string? modelName,
        Guid? agentId,
        bool useLocalTools,
        CancellationToken cancellationToken)
    {
        // The send path's head minus the conversation binding: an explicit model is a concrete user pick; blank takes
        // the installed-GGUF local default, and a null resolve flags the turn as needing an installed chat model.
        var userPickedConcreteModel = !string.IsNullOrWhiteSpace(modelName);
        var activeModel = userPickedConcreteModel
            ? modelName
            : await _localDefaultChatModelResolver.ResolveAsync((await _nodeSettingsStore.LoadAsync(cancellationToken)).DefaultModelName, cancellationToken);
        var requiresInstalledChatModel = activeModel is null;
        var effectiveAgentId = agentId ?? await _defaultAgentProvider.GetDefaultAgentIdAsync(cancellationToken);

        // No user turn exists yet, so a null retrieval query takes the playbook fast path, which never builds an
        // embedding client (as the Default Assistant offer read does).
        var resolution = await _turnResolver.ResolveAsync(activeModel, requiresInstalledChatModel, effectiveAgentId, retrievalQuery: null, userPickedConcreteModel, cancellationToken);
        var offer = await ChatToolOfferResolver.ResolveAsync(useLocalTools, resolution, _runtimeSettings, _localToolOfferProvider, _toolApprovalPolicy, cancellationToken);
        var systemPrompt = resolution.Resolved?.ResolvedSystemPrompt ?? await LocalChatDefaultPrompt.LoadAsync(_localChatOptions.Value);
        return new ResolvedEstimateInputs
        {
            Resolution = resolution,
            SystemPrompt = systemPrompt,
            Offer = offer
        };
    }

    /// <summary>What a fresh turn would resolve to: the turn, the system prompt it would send and its tool offer.</summary>
    internal sealed record ResolvedEstimateInputs
    {
        public required ChatTurnResolution Resolution { get; init; }

        public required string SystemPrompt { get; init; }

        public required ChatToolOffer Offer { get; init; }
    }

    /// <summary>
    ///     The window the first round is likely measured against, or <see langword="null" /> when the node does not know
    ///     the model.
    /// </summary>
    /// <remarks>
    ///     Mirrors <c>TurnPolicy.ResolveContextBudget</c> and <c>WithEffectiveContext</c>: a positive <c>num_ctx</c> is capped by a
    ///     known launched window, else taken as is. Without one: the launched window, a cold GGUF's ceiling capped at the node
    ///     default (under-reporting is the safe side), a declared window, else the node default. A cloud model without local
    ///     details takes the node default; any other is unknown.
    /// </remarks>
    private async Task<int?> ResolveWindowTokensAsync(string model, bool isCloud, int? numCtx, CancellationToken cancellationToken)
    {
        var details = await _modelDetailsResolver.ResolveAsync(model.Trim(), cancellationToken);
        if (details is LocalModelDetailsResolution.NoLocalDetails && !isCloud)
        {
            return null;
        }

        if (details is LocalModelDetailsResolution.Gguf { EffectiveContextTokens: int launched and > 0 })
        {
            return numCtx is int requestedWithin and > 0 ? Math.Min(requestedWithin, launched) : launched;
        }

        if (numCtx is int requested and > 0)
        {
            return requested;
        }

        var nodeDefault = await _runtimeSettings.GetDefaultContextTokensAsync(cancellationToken);
        var declared = details switch
        {
            LocalModelDetailsResolution.Gguf gguf => gguf.Descriptor.MaxContextTokens is int ceiling and > 0 ? Math.Min(ceiling, nodeDefault) : null,
            LocalModelDetailsResolution.External external => external.Registration.Model.ContextLength,
            LocalModelDetailsResolution.Ollama ollama => ollama.Details.MaxContextTokens,
            _ => null
        };

        return declared is > 0 ? declared : nodeDefault;
    }
}
