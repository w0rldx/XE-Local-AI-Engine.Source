namespace XE_Local_AI_Engine.Client.Services.Analysis.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IPlaybookAnalysisAgent" />: runs a <b>node-local</b> model (resolved per-model via
///     <see cref="ILocalModelProviderResolver" />, never the shared <see cref="IChatClient" /> singleton which can be a
///     cloud client) and forces a structured JSON response so each proposal carries its cited evidence + confidence.
///     Feedback comments are read into the model on-node only — they never cross the node boundary.
///     This type is intentionally not unit-tested against a live model; tests substitute a fake
///     <see cref="IPlaybookAnalysisAgent" />.
/// </summary>
internal sealed class DefaultPlaybookAnalysisAgent(
    ILocalModelProviderResolver providerResolver,
    IOptions<PlaybookAnalysisOptions> options,
    ILogger<DefaultPlaybookAnalysisAgent> logger) : IPlaybookAnalysisAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<DefaultPlaybookAnalysisAgent> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly PlaybookAnalysisOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    public async Task<IReadOnlyList<ProposedPlaybookAction>> ProposeAsync(FeedbackInsightsResult aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        // Route the configured analysis model to the runtime that serves it (persisted map, else the configured
        // default provider = ollama, so an un-repointed model behaves exactly as before). Node-local only — never the
        // cloud singleton.
        var provider = await _providerResolver.ResolveProviderForModelAsync(_options.ModelName, cancellationToken).ConfigureAwait(false);
        var selection = new LocalModelSelection
        {
            ModelName = _options.ModelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildSystemPrompt(_options.MaxProposals)),
            new(ChatRole.User, JsonSerializer.Serialize(ToPromptModel(aggregate), SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f
        };

        var response = await chatClient
                             .GetResponseAsync<AnalysisEnvelope>(messages, chatOptions, cancellationToken: cancellationToken)
                             .ConfigureAwait(false);

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
        return new ProposedPlaybookAction(proposal.Behavior ?? string.Empty,
            proposal.TriggerCondition,
            proposal.Scope,
            proposal.SourceFeedbackIds is null ? [] : [.. proposal.SourceFeedbackIds],
            proposal.Confidence);
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
