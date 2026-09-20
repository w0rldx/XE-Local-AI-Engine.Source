namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Default <see cref="IGoldenHarvestService" />, deterministic and model-free: it reads an agent's most-recent
///     thumbs-up sources, dedups against already-harvested messages and stages each fresh candidate inert.
/// </summary>
/// <remarks>
///     Staging runs through <see cref="IGoldenConversationService.CreateHarvestedAsync" />, so the same validation,
///     caps and encryption apply. The seeded rubric is the operator-approved answer, on the judge path, and the input
///     turns are the lead-up conversation serialized to match the eval runner's parse. Only counts and ids are logged.
/// </remarks>
internal sealed class GoldenHarvestService : IGoldenHarvestService
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
    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IGoldenConversationService _conversationService;
    private readonly IGoldenConversationStore _goldenStore;
    private readonly ILogger<GoldenHarvestService> _logger;
    private readonly GoldenHarvestOptions _options;

    private readonly IGoldenHarvestSourceStore _sourceStore;

    public GoldenHarvestService(
        IGoldenHarvestSourceStore sourceStore,
        IGoldenConversationStore goldenStore,
        IGoldenConversationService conversationService,
        IAgentDefinitionStore agentDefinitionStore,
        IOptions<GoldenHarvestOptions> options,
        ILogger<GoldenHarvestService> logger)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitionStore);
        _agentDefinitionStore = agentDefinitionStore;
        ArgumentNullException.ThrowIfNull(conversationService);
        _conversationService = conversationService;
        ArgumentNullException.ThrowIfNull(goldenStore);
        _goldenStore = goldenStore;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(sourceStore);
        _sourceStore = sourceStore;
    }

    public async Task<GoldenHarvestOutcome> HarvestAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentDefinitionStore.GetByIdAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return new GoldenHarvestOutcome { AgentExists = false, ThumbsUpScanned = 0, CreatedCount = 0, DuplicateCount = 0, SkippedCount = 0 };
        }

        var sources = await _sourceStore.ListThumbsUpSourcesAsync(agentId, _options.MaxThumbsUpScan, cancellationToken);
        var existing = new HashSet<Guid>(await _goldenStore.ListSourceMessageIdsByAgentAsync(agentId, cancellationToken));

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
                _ = await _conversationService.CreateHarvestedAsync(candidate, cancellationToken);
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

        return new GoldenHarvestOutcome
        {
            AgentExists = true,
            ThumbsUpScanned = sources.Count,
            CreatedCount = created,
            DuplicateCount = duplicate,
            SkippedCount = skipped
        };
    }

    private static GoldenConversationCreateInput BuildCandidate(Guid agentId, HarvestCandidateSource source, string firstUserTurnText)
    {
        return new GoldenConversationCreateInput
        {
            AgentDefinitionId = agentId,
            Title = BuildTitle(source, firstUserTurnText),
            InputTurns = SerializeTurns(source.PriorTurns),
            Assertion = null,
            Rubric = Truncate(RubricSeed + source.ApprovedAnswerText, MaxRubricLength),
            Enabled = false,
            Source = GoldenConversationSource.Harvested,
            SourceMessageId = source.MessageId,
            SourceConversationId = source.ConversationId
        };
    }

    private static string BuildTitle(HarvestCandidateSource source, string firstUserTurnText)
    {
        var label = string.IsNullOrWhiteSpace(source.ConversationTitle) ? firstUserTurnText : source.ConversationTitle;
        return Truncate(TitlePrefix + label, MaxTitleLength);
    }

    private static string SerializeTurns(IReadOnlyList<HarvestTurn> priorTurns)
    {
        var payload = priorTurns.Select(static turn => new GoldenTurnPayload { Role = turn.Role, Text = turn.Text }).ToArray();
        return JsonSerializer.Serialize(payload, InputTurnsSerializerOptions);
    }

    // Surrogate-pair-safe truncation: a split pair serializes a lone surrogate to U+FFFD, so StringInfo measures text
    // elements and the cut lands on the nearest element boundary at or below the cap.
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
    private sealed record GoldenTurnPayload
    {
        public required string Role { get; init; }

        public required string Text { get; init; }
    }
}
