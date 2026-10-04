namespace XE_Local_AI_Engine.Client.Services.Analysis.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IPlaybookAnalysisAgent" />: runs a <b>node-local</b> model (resolved per-model via
///     <see cref="ILocalModelProviderResolver" />, never the shared <see cref="IChatClient" /> singleton which can be a cloud client) and
///     forces a structured JSON response so each proposal carries its cited evidence + confidence.
/// </summary>
/// <remarks>
///     Feedback comments are read into the model on-node only — they never cross the node boundary. This type is intentionally not
///     unit-tested against a live model; tests substitute a fake <see cref="IPlaybookAnalysisAgent" />.
/// </remarks>
internal sealed class DefaultPlaybookAnalysisAgent : IPlaybookAnalysisAgent
{
    // A structured answer of a few short candidates needs a few hundred tokens; the cap only stops a runaway generation
    // from holding the single llama-server slot to the window while chat waits behind it.
    private const int MaxOutputTokens = 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DefaultPlaybookAnalysisAgent> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly PlaybookAnalysisOptions _options;

    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly INodeRuntimeSettings _runtimeSettings;

    public DefaultPlaybookAnalysisAgent(ILocalModelProviderResolver providerResolver,
        IOptions<PlaybookAnalysisOptions> options,
        INodeRuntimeSettings runtimeSettings,
        IModelTrustResolver modelTrustResolver,
        IServiceScopeFactory scopeFactory,
        ILogger<DefaultPlaybookAnalysisAgent> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(providerResolver);
        _providerResolver = providerResolver;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<IReadOnlyList<ProposedPlaybookAction>> ProposeAsync(FeedbackInsightsResult aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        // Route the configured analysis model to the runtime that serves it (persisted map, else the configured default provider, ollama, so an
        // un-repointed model behaves exactly as before). Node-local only — never the cloud singleton.
        var modelName = await _runtimeSettings.GetPlaybookAnalysisModelNameAsync(cancellationToken);
        if (!await BackgroundModelLocalityGuard.AllowAsync(modelName, "PlaybookAnalysisModelName", _modelTrustResolver, _logger, cancellationToken))
        {
            return [];
        }

        _logger.LogInformation("Playbook analysis for agent {AgentName} runs on model {ModelName}.", aggregate.AgentName, modelName);
        var provider = await _providerResolver.ResolveProviderForModelAsync(modelName, cancellationToken);
        var selection = new LocalModelSelection
        {
            ModelName = modelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection).WithProviderTelemetry();

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildSystemPrompt(_options.MaxProposals)),
            new(ChatRole.User, JsonSerializer.Serialize(ToPromptModel(aggregate), SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f,
            MaxOutputTokens = MaxOutputTokens
        };

        // Reasoning OFF, both halves at once and only on a thinking-capable model, exactly as the summarizer sends it.
        // The capability resolver is scoped and this agent a singleton, so it is resolved in a scope of its own.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var capabilities = await scope.ServiceProvider.GetRequiredService<IModelCapabilityResolver>().ResolveAsync(modelName, cancellationToken);
        if (capabilities.SupportsThinking)
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false,
                [InvocationAgentFactory.LlamaDisableThinkingMarkerKey] = true
            };
        }

        var response = await chatClient
            .GetResponseAsync<AnalysisEnvelope>(messages, chatOptions, cancellationToken: cancellationToken);

        if (!response.TryGetResult(out var envelope) || envelope?.Proposals is null)
        {
            _logger.LogWarning("Playbook analysis model returned no parseable proposals for agent {AgentName}.", aggregate.AgentName);
            return [];
        }

        return [.. envelope.Proposals.Select(ToProposedAction)];
    }

    private static ProposedPlaybookAction ToProposedAction(AnalysisProposal proposal)
    {
        // Pass the raw proposal through; the service validates evidence/confidence and rejects anything invalid.
        return new ProposedPlaybookAction
        {
            Behavior = proposal.Behavior ?? string.Empty,
            TriggerCondition = proposal.TriggerCondition,
            Scope = proposal.Scope,
            SourceFeedbackIds = proposal.SourceFeedbackIds is null ? [] : [.. proposal.SourceFeedbackIds],
            Confidence = proposal.Confidence
        };
    }

    private static object ToPromptModel(FeedbackInsightsResult aggregate)
    {
        // Hand the model only what it needs to reason + cite: the counts, the per-tool facet, and each exemplar with
        // its id (already capped/truncated by the feedback-insights service — no raw store read here).
        return new
        {
            aggregate.AgentName,
            aggregate.Overall,
            aggregate.ByTool,
            Exemplars = aggregate.Exemplars
                                 .Select(static exemplar => new
                                 {
                                     exemplar.MessageId,
                                     exemplar.ConversationId,
                                     exemplar.Rating,
                                     exemplar.Comment
                                 })
                                 .ToArray()
        };
    }

    private static string BuildSystemPrompt(int maxProposals)
    {
        return $$"""
                 You analyze user feedback for one AI agent and propose concrete playbook actions that would improve it.
                 You are given a JSON aggregate: overall up/down counts, a per-tool breakdown, and comment exemplars. Each
                 exemplar has a messageId and a conversationId.

                 Propose at most {{maxProposals}} actions. Return ONLY a JSON object of the form:
                 { "proposals": [ { "behavior": string, "triggerCondition": string|null, "scope": string|null,
                   "sourceFeedbackIds": [guid, ...], "confidence": number } ] }

                 Rules:
                 - "behavior" is a single concrete instruction to add to the agent's system prompt.
                 - "sourceFeedbackIds" MUST list the messageId(s) (or conversationId(s)) of the exemplars that justify the
                   action. Every id MUST come from the provided aggregate — never invent an id. An action with no citation
                   is invalid; do not emit it.
                 - "confidence" is a number between 0 and 1.
                 - Base proposals on recurring patterns across the feedback, not a single comment.
                 - If the feedback does not justify any action, return { "proposals": [] }.
                 """;
    }

    // Positional records: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults),
    // and the constructor counts as the assignment so the unassigned-auto-property analyzer stays quiet.
    private sealed record AnalysisEnvelope(List<AnalysisProposal>? Proposals);

    private sealed record AnalysisProposal(
        string? Behavior,
        string? TriggerCondition,
        string? Scope,
        List<Guid>? SourceFeedbackIds,
        double Confidence);
}
