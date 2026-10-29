namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Default <see cref="IGraphWorkflowDesignerService" />, on drafting's pattern: one structured-output call per attempt,
///     the drafting admission gate and model eligibility, and a timeout from the node setting.
/// </summary>
/// <remarks>
///     Each proposal is validated (the definition's real <c>AllowsActions</c>) and linted; findings go back to the model up to
///     <see cref="MaxFixUpRetries" /> times, a change turn without a proposal is nudged once, and a proposal still invalid is
///     returned marked invalid. A node-local model runs Transient with thinking off; any other model takes the node's cloud
///     routing client, only while <see cref="CloudLocalDataGate" /> lets the workflow leave the node. Model text is never logged.
/// </remarks>
internal sealed class GraphWorkflowDesignerService : IGraphWorkflowDesignerService
{
    /// <summary>How many times findings are fed back before the proposal is returned as it stands.</summary>
    internal const int MaxFixUpRetries = 2;

    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    /// <summary>
    ///     The response format every attempt sends: every member required and nullable in declaration order, no extra
    ///     members, and no boolean subschema, which llama.cpp's schema-to-grammar refuses with an HTTP 500.
    /// </summary>
    /// <remarks>
    ///     Built here rather than by <c>GetResponseAsync&lt;T&gt;</c>, whose schema leaves members optional and writes a raw
    ///     <see cref="JsonElement" /> as <c>true</c>. A config is any object; a condition value is any JSON scalar.
    /// </remarks>
    internal static readonly ChatResponseFormatJson ResponseFormat = ChatResponseFormat.ForJsonSchema(
        AIJsonUtilities.CreateJsonSchema(typeof(GraphWorkflowDesignerEnvelope),
            serializerOptions: EnvelopeOptions,
            inferenceOptions: new AIJsonSchemaCreateOptions
            {
                TransformSchemaNode = TransformSchemaNode,
                TransformOptions = new AIJsonSchemaTransformOptions
                {
                    RequireAllProperties = true,
                    DisallowAdditionalProperties = true
                }
            }),
        "graph_workflow_designer_turn",
        "A reply, and the whole proposed graph or null.");

    /// <summary>The proposal as a graph document: absent members stay absent, so the parser applies its own defaults.</summary>
    private static readonly JsonSerializerOptions GraphOptions = new(EnvelopeOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DraftAdmissionGate _admissionGate;
    private readonly IAgentDefinitionService _agents;
    private readonly IModelCapabilityResolver _capabilities;
    private readonly IChatClient _cloudChatClient;
    private readonly IGraphWorkflowDefinitionService _definitions;
    private readonly DraftingOptions _draftingOptions;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IOptions<GraphWorkflowOptions> _graphOptions;
    private readonly ILogger<GraphWorkflowDesignerService> _logger;
    private readonly IModelClassificationStore _modelClassificationStore;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly IOllamaModelService _ollamaModelService;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;
    private readonly IModelTrustResolver _trust;

    public GraphWorkflowDesignerService(IGraphWorkflowDefinitionService definitions,
        IAgentDefinitionService agents,
        ILocalModelProviderResolver providerResolver,
        IGgufModelStore ggufModelStore,
        IModelClassificationStore modelClassificationStore,
        IOllamaModelService ollamaModelService,
        IModelCapabilityResolver capabilities,
        IModelTrustResolver trust,
        INodeRuntimeSettings runtimeSettings,
        IChatClient cloudChatClient,
        DraftAdmissionGate admissionGate,
        IOptions<DraftingOptions> draftingOptions,
        IOptions<GraphWorkflowOptions> graphOptions,
        INodeSettingsStore nodeSettingsStore,
        TimeProvider timeProvider,
        ILogger<GraphWorkflowDesignerService> logger)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _modelClassificationStore = modelClassificationStore ?? throw new ArgumentNullException(nameof(modelClassificationStore));
        _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _cloudChatClient = cloudChatClient ?? throw new ArgumentNullException(nameof(cloudChatClient));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _draftingOptions = (draftingOptions ?? throw new ArgumentNullException(nameof(draftingOptions))).Value;
        _graphOptions = graphOptions ?? throw new ArgumentNullException(nameof(graphOptions));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GraphWorkflowDesignerResult> TurnAsync(GraphWorkflowDesignerTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ModelName) || request.Messages is not [.., { Role: GraphWorkflowDesignerRole.User }])
        {
            return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.InvalidRequest, "A model and a conversation ending in your message are required.");
        }

        // One budget for everything the operator sends into the prompt, the working graph included, checked BEFORE the gate so
        // an oversized request never holds the slot.
        if (request.Messages.Sum(static message => message.Text.Length) + (request.GraphJson?.Length ?? 0) > _draftingOptions.MaxPromptChars)
        {
            return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.InvalidRequest,
                $"The workflow plus the conversation is too large for the designer (over {_draftingOptions.MaxPromptChars} characters). "
                + "Start a new chat, or work on a smaller workflow.");
        }

        var localProviderName = await DraftModelEligibility.ResolveEligibleProviderAsync(request.ModelName,
            _modelClassificationStore,
            _ggufModelStore,
            _ollamaModelService,
            cancellationToken);
        if (localProviderName is null && await RefuseRemoteModelAsync(request.ModelName, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var allowsActions = request.DefinitionId is { } definitionId && (await _definitions.GetAsync(definitionId, cancellationToken)).AllowsActions;

        IDisposable? lease = null;
        try
        {
            if (!_admissionGate.TryAcquire(out lease))
            {
                return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.NodeBusy, "The node is running another task; try again once it finishes.");
            }

            return await GenerateAsync(request, localProviderName, allowsActions, cancellationToken);
        }
        finally
        {
            // Unconditional release on every path — a leaked slot would wedge drafting and the designer until a restart.
            lease?.Dispose();
        }
    }

    /// <summary>
    ///     The refusal for a model that is not an eligible node-local chat model, or <see langword="null" /> when the cloud
    ///     path may serve it.
    /// </summary>
    /// <remarks>
    ///     Only a model that resolves as leaving the node, or an external connection declared local, reaches the cloud path:
    ///     any other name would fall through to the default local provider. Leaving the node needs the connection's
    ///     local-data grant or the node switch, the same rule as a graph edge and chat; there is no silent fallback.
    /// </remarks>
    private async Task<GraphWorkflowDesignerResult?> RefuseRemoteModelAsync(string modelName, CancellationToken cancellationToken)
    {
        var locality = await _trust.ResolveAsync(modelName, cancellationToken);
        if (locality == ModelTrustLocality.Local && await _trust.TryResolveExternalAsync(modelName, cancellationToken) is null)
        {
            return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.ModelNotEligible,
                "The selected model is neither an installed chat model served by a node-local runtime nor a cloud model.");
        }

        var grants = await _trust.ResolveCloudGrantsAsync(modelName, cancellationToken);
        return await CloudLocalDataGate.WithholdsAsync(leavesNode: locality != ModelTrustLocality.Local, grants, _runtimeSettings, cancellationToken)
            ? GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.CloudLocalDataWithheld,
                $"Model '{modelName}' is not a node-local model, and the workflow is local data. Grant local data on the model's external connection, "
                + "or turn on 'Let cloud models read local data' in Node Settings → Privacy & updates, to use it here.")
            : null;
    }

    private async Task<GraphWorkflowDesignerResult> GenerateAsync(GraphWorkflowDesignerTurnRequest request,
        string? localProviderName,
        bool allowsActions,
        CancellationToken cancellationToken)
    {
        // A model request like any other: the node's live "Maximum message request timeout", unless drafting pins its own.
        var timeout = _draftingOptions.GenerationTimeout
                      ?? TimeSpan.FromSeconds((await _nodeSettingsStore.LoadAsync(cancellationToken)).MaxMessageRequestTimeoutSeconds);
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        generation.CancelAfter(timeout);

        IDisposable? ownedClient = null;
        try
        {
            IChatClient chatClient;
            var chatOptions = new ChatOptions
            {
                ModelId = request.ModelName,
                ResponseFormat = ResponseFormat,
                Temperature = 0.3f,
                MaxOutputTokens = _draftingOptions.MaxOutputTokens
            };
            if (localProviderName is not null)
            {
                var provider = await _providerResolver.ResolveProviderForModelAsync(request.ModelName, generation.Token);

                // The resolver routes an unmapped model to the DEFAULT provider, so a runtime other than the one eligibility cleared is refused.
                if (!string.Equals(provider.ProviderName, localProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.ModelNotEligible,
                        "The selected model is not an installed chat model served by a node-local runtime.");
                }

                // Transient, the drafting way: a model loaded only for this turn is unloaded soon after by the runtime's idle reaper.
                chatClient = provider.CreateChatClient(new LocalModelSelection
                {
                    ModelName = request.ModelName,
                    ProviderName = provider.ProviderName,
                    ResidencyIntent = ModelResidencyIntent.Transient
                }).WithProviderTelemetry();
                ownedClient = chatClient;

                // Thinking off: the lint and the retries carry the quality, and thinking cost 30 to 130 s per attempt.
                if ((await _capabilities.ResolveAsync(request.ModelName, generation.Token)).SupportsThinking)
                {
                    chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["think"] = false,
                        [InvocationAgentFactory.LlamaDisableThinkingMarkerKey] = true
                    };
                }
            }
            else
            {
                // The node's routing client: a cloud id goes to its provider and an external id to its connection. Owned by DI.
                chatClient = _cloudChatClient;
            }

            return await RunAttemptsAsync(request, chatClient, chatOptions, allowsActions, generation.Token);
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException)
        {
            // The provider refused or failed the request. Only its status is kept: the body may carry model or prompt text.
            var status = exception switch
            {
                ClientResultException { Status: > 0 } result => result.Status,
                HttpRequestException { StatusCode: { } code } => (int)code,
                _ => (int?)null
            };
            _logger.LogWarning("Workflow designer turn for model {ModelName} failed at the model provider (HTTP status {Status}).", request.ModelName, status);
            return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.ProviderFailed,
                status is null
                    ? "The model provider failed the request. Try again, or pick another model."
                    : $"The model provider failed the request (HTTP {status}). Try again, or pick another model.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Workflow designer turn for model {ModelName} exceeded the {TimeoutSeconds}s budget.", request.ModelName, timeout.TotalSeconds);
            return GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.Unparseable, "The model did not finish within the generation budget.");
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private async Task<GraphWorkflowDesignerResult> RunAttemptsAsync(GraphWorkflowDesignerTurnRequest request,
        IChatClient chatClient,
        ChatOptions chatOptions,
        bool allowsActions,
        CancellationToken cancellationToken)
    {
        var maxNodes = _graphOptions.Value.MaxNodesPerDefinition;
        var tools = (await _definitions.ListToolsAsync(cancellationToken)).Where(tool => allowsActions || !tool.IsAction).ToList();
        var systemPrompt = GraphWorkflowDesignerPrompt.Build(tools,
            await _ggufModelStore.ListInstalledModelsAsync(cancellationToken),
            await _agents.ListAsync(cancellationToken),
            string.IsNullOrWhiteSpace(request.GraphJson) ? "(empty)" : request.GraphJson,
            request.SelectedNodeKey,
            request.Intent);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            // The client trims its history from the front, which can leave an assistant message first; chat templates that
            // require the user to speak first refuse that, so leading assistant messages are dropped.
            .. request.Messages.SkipWhile(static message => message.Role == GraphWorkflowDesignerRole.Assistant)
                      .Select(static message => new ChatMessage(message.Role == GraphWorkflowDesignerRole.User ? ChatRole.User : ChatRole.Assistant, message.Text))
        ];

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        long? inputTokens = null;
        long? outputTokens = null;
        var fixUpsLeft = MaxFixUpRetries;
        var nudged = false;
        GraphWorkflowDesignerTurn? last = null;
        while (true)
        {
            attempts++;
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            inputTokens = Add(inputTokens, response.Usage?.InputTokenCount);
            outputTokens = Add(outputTokens, response.Usage?.OutputTokenCount);

            var envelope = ParseEnvelope(response.Text);
            var replyOnly = envelope?.Proposal is null || request.Intent == GraphWorkflowDesignerIntent.Ask;

            // A reply-only answer with no reply is as unusable as no envelope: the client would store an empty message
            // its own next request is refused for. So is a proposal holding a null node or edge, which no graph can say.
            if (envelope is null
                || (replyOnly && string.IsNullOrWhiteSpace(envelope.Reply))
                || (!replyOnly && HasNullElement(envelope.Proposal!)))
            {
                if (fixUpsLeft-- > 0)
                {
                    messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                    messages.Add(new ChatMessage(ChatRole.User, "That was not a usable answer. Answer again with the JSON envelope {\"reply\": ..., \"proposal\": ...} only, and a non-empty reply."));
                    continue;
                }

                // Deliberately text-free: the model's output is never echoed into a log line or a failure result.
                _logger.LogWarning("Workflow designer turn for model {ModelName} returned no usable envelope.", request.ModelName);
                return last is null
                    ? GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.Unparseable, "The model returned no usable answer. Try again, or pick another model.")
                    : GraphWorkflowDesignerResult.Success(last);
            }

            var reply = CapReply(envelope.Reply?.Trim() ?? string.Empty);
            if (replyOnly)
            {
                if (request.Intent == GraphWorkflowDesignerIntent.Change && !nudged)
                {
                    nudged = true;
                    messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(envelope, EnvelopeOptions)));
                    messages.Add(new ChatMessage(ChatRole.User, "You were asked to change the workflow. Answer again with the whole proposed graph in \"proposal\"."));
                    continue;
                }

                return GraphWorkflowDesignerResult.Success(Turn(reply, proposalGraphJson: null, [], validation: null));
            }

            // A null array reads as empty, so the parser answers it like any other graph with nothing in it.
            var proposalJson = JsonSerializer.Serialize(new GraphWorkflowDesignerGraph
                {
                    SchemaVersion = envelope.Proposal!.SchemaVersion,
                    Kind = envelope.Proposal.Kind,
                    Chat = envelope.Proposal.Chat,
                    Nodes = envelope.Proposal.Nodes ?? [],
                    Edges = envelope.Proposal.Edges ?? []
                },
                GraphOptions);
            var validation = await _definitions.ValidateAsync(proposalJson, allowsActions, cancellationToken);
            var lint = LintFindings(proposalJson, maxNodes);
            var changes = GraphWorkflowDesignerChanges.Compute(request.GraphJson, proposalJson, maxNodes);
            last = Turn(reply.Length > 0 ? reply : ChangeSummary(changes), proposalJson, changes, WithLintAsErrors(validation, lint));
            if ((validation.IsValid && lint.Count == 0) || fixUpsLeft-- <= 0)
            {
                return GraphWorkflowDesignerResult.Success(last);
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(envelope, EnvelopeOptions)));
            messages.Add(new ChatMessage(ChatRole.User, Feedback(validation.Errors, lint)));
        }

        GraphWorkflowDesignerTurn Turn(string reply, string? proposalGraphJson, IReadOnlyList<GraphWorkflowDesignerChange> changes, GraphWorkflowValidationResult? validation) =>
            new()
            {
                Reply = reply,
                ProposalGraphJson = proposalGraphJson,
                Changes = changes,
                Validation = validation,
                Attempts = attempts,
                Model = request.ModelName,
                GeneratedAtUtc = _timeProvider.GetUtcNow(),
                DurationMs = stopwatch.ElapsedMilliseconds,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
    }

    /// <summary>
    ///     The designer's own report: lint findings left after the retries move to the errors, so the proposal reads as
    ///     invalid and cannot be accepted. The validate and save routes keep them as warnings.
    /// </summary>
    private static GraphWorkflowValidationResult WithLintAsErrors(GraphWorkflowValidationResult validation, IReadOnlyList<GraphWorkflowValidationError> lint) =>
        lint.Count == 0
            ? validation
            : new GraphWorkflowValidationResult
            {
                Errors = [.. validation.Errors, .. lint.Where(finding => !validation.Errors.Contains(finding))],
                Warnings = [.. validation.Warnings.Where(warning => !lint.Contains(warning))]
            };

    /// <summary>A reply within the message cap, cut with an ellipsis, because the client replays it as the next turn's history.</summary>
    private static string CapReply(string reply)
    {
        if (reply.Length <= GraphWorkflowDesignerTurnRequest.MaxMessageLength)
        {
            return reply;
        }

        // One unit for the ellipsis, and never a cut between the halves of a surrogate pair.
        var keep = GraphWorkflowDesignerTurnRequest.MaxMessageLength - 1;
        keep -= char.IsHighSurrogate(reply[keep - 1]) ? 1 : 0;
        return string.Concat(reply.AsSpan(0, keep).TrimEnd(), "…");
    }

    /// <summary>A fixed reply for a proposal the model sent without one, so the conversation never stores an empty message.</summary>
    private static string ChangeSummary(IReadOnlyList<GraphWorkflowDesignerChange> changes)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"Proposed changes: {Count("added")} added, {Count("modified")} modified, {Count("removed")} removed.");

        int Count(string change) => changes.Count(entry => string.Equals(entry.Change, change, StringComparison.Ordinal));
    }

    /// <summary>Raw JSON gets an explicit object or scalar schema; a node's or edge's identity strings lose their <c>null</c>.</summary>
    private static JsonNode TransformSchemaNode(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (context.TypeInfo.Type == typeof(JsonElement) || context.TypeInfo.Type == typeof(JsonElement?))
        {
            return RawJsonSchema(context.DeclaringType == typeof(GraphWorkflowDesignerCondition));
        }

        return IsIdentity(context) ? NonNull(schema) : schema;
    }

    /// <summary>
    ///     The members the wire graph declares as non-null strings: a node's key and kind, an edge's key and ends. The
    ///     model must write them, and the client's contract would refuse a null.
    /// </summary>
    private static bool IsIdentity(AIJsonSchemaCreateContext context) =>
        context.PropertyInfo?.Name is { } name
        && ((context.DeclaringType == typeof(GraphWorkflowDesignerNode) && name is "key" or "kind")
            || (context.DeclaringType == typeof(GraphWorkflowDesignerEdge) && name is "key" or "from" or "to"));

    /// <summary>The schema with <c>null</c> taken out of its type and its enum; still required, in the same place.</summary>
    private static JsonNode NonNull(JsonNode schema)
    {
        if (schema is not JsonObject members)
        {
            return schema;
        }

        if (members["type"] is JsonArray types)
        {
            members["type"] = types.Select(static type => type?.GetValue<string>()).FirstOrDefault(static type => type is not null and not "null");
        }

        if (members["enum"] is JsonArray values)
        {
            members["enum"] = new JsonArray([.. values.Where(static value => value is not null).Select(static value => value!.DeepClone())]);
        }

        return members;
    }

    private static JsonNode RawJsonSchema(bool scalar) =>
        new JsonObject
        {
            ["type"] = scalar ? new JsonArray("string", "number", "boolean", "null") : new JsonArray("object", "null")
        };

    private static bool HasNullElement(GraphWorkflowDesignerGraph proposal) =>
        (proposal.Nodes?.Any(static node => node is null) ?? false) || (proposal.Edges?.Any(static edge => edge is null) ?? false);

    /// <summary>The envelope in the model's text, or <see langword="null" /> when the text is not one.</summary>
    private static GraphWorkflowDesignerEnvelope? ParseEnvelope(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<GraphWorkflowDesignerEnvelope>(text, EnvelopeOptions);
        }
        catch (JsonException)
        {
            // A JSON shape the envelope cannot hold is the same outcome as no JSON at all.
            return null;
        }
    }

    /// <summary>The semantic lint findings on a proposal the parser accepts; none when it does not parse, since its errors come first.</summary>
    private static IReadOnlyList<GraphWorkflowValidationError> LintFindings(string proposalJson, int maxNodes)
    {
        try
        {
            return GraphWorkflowGraph.Parse(proposalJson, maxNodes, []).SemanticLintWarnings;
        }
        catch (GraphWorkflowValidationException)
        {
            return [];
        }
    }

    private static string Feedback(IReadOnlyList<GraphWorkflowValidationError> errors, IReadOnlyList<GraphWorkflowValidationError> lint)
    {
        var builder = new StringBuilder("Your proposal does not pass the node's checks. Fix every item below and answer again with the whole graph.");
        foreach (var finding in errors.Concat(lint))
        {
            _ = builder.AppendLine().Append("- ");
            if (finding.Key is { } key)
            {
                _ = builder.Append('[').Append(key).Append("] ");
            }

            _ = builder.Append(finding.Message);
        }

        return builder.ToString();
    }

    private static long? Add(long? total, long? value) =>
        value is null ? total : (total ?? 0) + value;
}
