namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Default <see cref="IGoldenHarvestService" /> (deterministic, no model). Reads an agent's most-recent
///     thumbs-up sources via <see cref="IGoldenHarvestSourceStore" />, dedups against already-harvested source messages,
///     and stages each fresh candidate inert through <see cref="IGoldenConversationService.CreateHarvestedAsync" /> (so
///     the same validation/caps/encryption apply). The seeded rubric is the operator-approved answer (judge path); the input
///     turns are the lead-up conversation serialized as camelCase {role,text} to match the eval runner's parse. No turn
///     or answer text is ever logged — only counts and ids.
/// </summary>
internal sealed class GoldenHarvestService(
    IGoldenHarvestSourceStore sourceStore,
    IGoldenConversationStore goldenStore,
    IGoldenConversationService conversationService,
    IAgentDefinitionStore agentDefinitionStore,
    IOptions<GoldenHarvestOptions> options,
    ILogger<GoldenHarvestService> logger) : IGoldenHarvestService
{
    // Title prefix marking a harvested candidate + the rubric seed template (judge path: the approved answer is the scoring
    // signal). The title cap mirrors GoldenConversationService.MaxTitleLength; the rubric cap mirrors MaxRubricLength.
    private const string TitlePrefix = "Harvested: ";
    private const string RubricSeed = "The response should be consistent with this operator-approved answer:\n\n";
    private const int MaxTitleLength = 200;
    private const int MaxRubricLength = 20_000;

    // Cache the serializer options statically (CA1869). Web defaults serialize the payload as camelCase {role,text},
    // matching PlaybookEvalService's InputTurn parse.
    private static readonly JsonSerializerOptions InputTurnsSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IGoldenConversationService _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
    private readonly IGoldenConversationStore _goldenStore = goldenStore ?? throw new ArgumentNullException(nameof(goldenStore));
    private readonly ILogger<GoldenHarvestService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly GoldenHarvestOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly IGoldenHarvestSourceStore _sourceStore = sourceStore ?? throw new ArgumentNullException(nameof(sourceStore));

    public async Task<GoldenHarvestOutcome> HarvestAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentDefinitionStore.GetByIdAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return new GoldenHarvestOutcome(AgentExists: false, ThumbsUpScanned: 0, CreatedCount: 0, DuplicateCount: 0, SkippedCount: 0);
        }

        var sources = await _sourceStore.ListThumbsUpSourcesAsync(agentId, _options.MaxThumbsUpScan, cancellationToken).ConfigureAwait(false);
        var existing = new HashSet<Guid>(await _goldenStore.ListSourceMessageIdsByAgentAsync(agentId, cancellationToken).ConfigureAwait(false));

        var duplicate = 0;
        var skipped = 0;
        var created = 0;

        foreach (var source in sources)
        {
            if (existing.Contains(source.MessageId))
            {
                // Already harvested: re-running harvest never double-proposes the same thumbs-up.
                duplicate++;
                continue;
            }

            var firstUserTurn = source.PriorTurns.FirstOrDefault(turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase));
            if (firstUserTurn is null)
            {
                // No lead-up user turn: the thumbs-up is unusable as an input conversation, so skip it.
                skipped++;
                continue;
            }

            if (created >= _options.MaxProposals)
            {
                // Hard server cap: stop persisting once the per-run proposal limit is hit.
                break;
            }

            var candidate = BuildCandidate(agentId, source, firstUserTurn.Text);

            try
            {
                _ = await _conversationService.CreateHarvestedAsync(candidate, cancellationToken).ConfigureAwait(false);
                created++;
            }
            catch (PlaybookActionValidationException exception)
            {
                // A candidate rejected at the create boundary (e.g. serialized turns over the InputTurns cap) is skipped,
                // not failed. Log the reason WITHOUT any turn/answer text — only the message id and the validation message.
                skipped++;
                _logger.LogWarning(exception, "Skipped a harvested golden candidate for message {MessageId}: rejected at the create boundary.", source.MessageId);
            }
        }

        return new GoldenHarvestOutcome(AgentExists: true,
            sources.Count,
            created,
            duplicate,
            skipped);
    }

    private static GoldenConversationCreateInput BuildCandidate(Guid agentId, HarvestCandidateSource source, string firstUserTurnText)
    {
        return new GoldenConversationCreateInput(agentId,
            BuildTitle(source, firstUserTurnText),
            SerializeTurns(source.PriorTurns),
            Assertion: null,
            Truncate(RubricSeed + source.ApprovedAnswerText, MaxRubricLength),
            Enabled: false,
            GoldenConversationSource.Harvested,
            source.MessageId,
            source.ConversationId);
    }

    private static string BuildTitle(HarvestCandidateSource source, string firstUserTurnText)
    {
        var label = string.IsNullOrWhiteSpace(source.ConversationTitle) ? firstUserTurnText : source.ConversationTitle;
        return Truncate(TitlePrefix + label, MaxTitleLength);
    }

    private static string SerializeTurns(IReadOnlyList<HarvestTurn> priorTurns)
    {
        var payload = priorTurns.Select(static turn => new GoldenTurnPayload(turn.Role, turn.Text)).ToArray();
        return JsonSerializer.Serialize(payload, InputTurnsSerializerOptions);
    }

    // Surrogate-pair-safe truncation (mirrors FeedbackInsightsService.Truncate): never split a surrogate pair, which
    // would serialize a lone surrogate to U+FFFD. StringInfo measures text elements; we cut on the nearest element
    // boundary at or below the cap.
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var cut = 0;
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + enumerator.GetTextElement().Length;
            if (next > maxLength)
            {
                break;
            }

            cut = next;
        }

        return value[..cut];
    }

    /// <summary>STJ payload for one serialized input turn — positional record so Web defaults emit camelCase {role,text}.</summary>
    private sealed record GoldenTurnPayload(string Role, string Text);
}
