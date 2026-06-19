namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Eval;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="IPlaybookEvalService" />. Re-runs the real agent loop over the
///     agent's golden conversation set with the candidate prompt (baseline + the Suggested action) vs the current
///     baseline, scores each case (assertion or node-local judge), and persists a plaintext
///     <see cref="PlaybookEvalResult" /> on the action so the promote gate can decide. Resolves ONE node-local
///     <see cref="IChatClient" /> for the whole run (never the shared/cloud singleton) and passes it into the runner +
///     judge, so golden text + agent output never leave the node. Offline / batch only — never on the chat hot path.
/// </summary>
internal sealed class PlaybookEvalService(
    IPlaybookActionService playbookActionService,
    IPlaybookActionStore playbookActionStore,
    IAgentDefinitionStore agentDefinitionStore,
    IGoldenConversationStore goldenConversationStore,
    IPlaybookEvalAgentRunner evalAgentRunner,
    IPlaybookEvalJudge evalJudge,
    ILocalModelProviderResolver providerResolver,
    TimeProvider timeProvider,
    IOptions<PlaybookEvalOptions> options,
    ILogger<PlaybookEvalService> logger) : IPlaybookEvalService
{
    private static readonly JsonSerializerOptions InputTurnsSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IPlaybookEvalAgentRunner _evalAgentRunner = evalAgentRunner ?? throw new ArgumentNullException(nameof(evalAgentRunner));
    private readonly IPlaybookEvalJudge _evalJudge = evalJudge ?? throw new ArgumentNullException(nameof(evalJudge));
    private readonly IGoldenConversationStore _goldenConversationStore = goldenConversationStore ?? throw new ArgumentNullException(nameof(goldenConversationStore));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
    private readonly ILogger<PlaybookEvalService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly PlaybookEvalOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));
    private readonly IPlaybookActionStore _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PlaybookEvalOutcome> RunEvalAsync(Guid agentId, Guid actionId, CancellationToken cancellationToken = default)
    {
        // Same ownership + Suggested + Analysis guard as the review paths — a missing/cross-agent/non-pending action
        // surfaces ActionFound == false so the endpoint 404s.
        var suggested = await _playbookActionService.LoadPendingSuggestionAsync(agentId, actionId, cancellationToken).ConfigureAwait(false);
        if (suggested is null)
        {
            return new PlaybookEvalOutcome(false, null);
        }

        var agent = await _agentDefinitionStore.GetByIdAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return new PlaybookEvalOutcome(false, null);
        }

        // Baseline = the agent's current resolved prompt (Instructions + Enabled actions). Candidate = baseline + the
        // Suggested action's behaviour. The gate measures the marginal effect of promoting THIS action.
        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(agentId, cancellationToken).ConfigureAwait(false);

        // Mirror ListEnabledByAgentAsync ordering (Priority, then CreatedAtUtc): once the Suggested action is promoted
        // it is re-ordered by that same key, so the candidate prompt must place it per priority — not merely append it
        // last — for the eval to score it in the SAME position the post-promotion injection will. The baseline stays
        // Compose(Instructions, enabled) since `enabled` is already store-ordered.
        var candidateActions = enabled
                               .Append(suggested)
                               .OrderBy(static action => action.Priority)
                               .ThenBy(static action => action.CreatedAtUtc)
                               .ToList();
        var baselinePrompt = PlaybookPromptComposer.Compose(agent.Instructions, enabled);
        var candidatePrompt = PlaybookPromptComposer.Compose(agent.Instructions, candidateActions);

        var goldenCases = await _goldenConversationStore.ListEnabledByAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        var goldenCaseTotal = goldenCases.Count;

        // Empty golden set never passes (no-regression is unprovable with zero cases) — persist a failing result so the
        // gap is visible (promote stays blocked) rather than silently waved through.
        if (goldenCases.Count == 0)
        {
            _logger.LogWarning("Eval for agent {AgentId} action {ActionId} has no golden cases; recording a failing result (needs golden cases).", agentId, actionId);
            return await PersistAsync(agentId, actionId, BuildEmptyResult(suggested.Version), cancellationToken).ConfigureAwait(false);
        }

        if (goldenCases.Count > _options.MaxGoldenCases)
        {
            _logger.LogWarning("Eval for agent {AgentId} has {Count} golden cases; truncating to {Max}.", agentId, goldenCases.Count, _options.MaxGoldenCases);
            goldenCases = [.. goldenCases.Take(_options.MaxGoldenCases)];
        }

        // Route the configured eval model to the runtime that serves it (persisted map, else §6.1 default = ollama —
        // an un-repointed model behaves exactly as before). Node-local only — never the shared/cloud singleton.
        var provider = await _providerResolver.ResolveProviderForModelAsync(_options.ModelName, cancellationToken).ConfigureAwait(false);
        var selection = new LocalModelSelection
        {
            ModelName = _options.ModelName,
            ProviderName = provider.ProviderName
        };

        // One node-local client for the whole run (IChatClient is IDisposable — dispose it; never the shared singleton).
        using var chatClient = provider.CreateChatClient(selection);

        var caseResults = new List<PlaybookEvalCaseResult>(goldenCases.Count);
        foreach (var goldenCase in goldenCases)
        {
            caseResults.Add(await ScoreCaseAsync(goldenCase, baselinePrompt, candidatePrompt, chatClient, cancellationToken).ConfigureAwait(false));
        }

        var result = BuildResult(suggested.Version, goldenCaseTotal, caseResults);
        return await PersistAsync(agentId, actionId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybookEvalCaseResult> ScoreCaseAsync(GoldenConversationRecord goldenCase,
        string baselinePrompt,
        string candidatePrompt,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var turns = ParseInputTurns(goldenCase);

        var baselineText = await _evalAgentRunner.RunAsync(chatClient, baselinePrompt, turns, cancellationToken).ConfigureAwait(false);
        var candidateText = await _evalAgentRunner.RunAsync(chatClient, candidatePrompt, turns, cancellationToken).ConfigureAwait(false);

        var baselineScore = await _evalJudge.ScoreAsync(goldenCase, baselineText, chatClient, cancellationToken).ConfigureAwait(false);
        var candidateScore = await _evalJudge.ScoreAsync(goldenCase, candidateText, chatClient, cancellationToken).ConfigureAwait(false);

        // Regression criterion (§6 #3): a case the baseline passed and the candidate fails is a regression.
        var regressed = baselineScore.Pass && !candidateScore.Pass;

        // Record the CANDIDATE's scoring path — the candidate is the thing under evaluation. (Baseline and candidate
        // agree today since both score the same golden case, but the candidate is the correct field to attribute.)
        return new PlaybookEvalCaseResult(goldenCase.Id, candidateScore.ScoredBy, baselineScore.Pass, candidateScore.Pass, regressed);
    }

    private IReadOnlyList<ChatMessage> ParseInputTurns(GoldenConversationRecord goldenCase)
    {
        InputTurn[]? turns;
        try
        {
            turns = JsonSerializer.Deserialize<InputTurn[]>(goldenCase.InputTurns, InputTurnsSerializerOptions);
        }
        catch (JsonException exception)
        {
            // A golden case whose turns cannot be parsed is unusable; log and treat it as an empty conversation so the
            // run still completes (the case will score as a fail, never silently pass).
            _logger.LogWarning(exception, "Failed to parse golden case {GoldenCaseId} input turns; treating as empty.", goldenCase.Id);
            return [];
        }

        if (turns is null)
        {
            return [];
        }

        return [.. turns.Select(static turn => new ChatMessage(MapRole(turn.Role), turn.Text ?? string.Empty))];
    }

    private static ChatRole MapRole(string? role)
    {
        return string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User;
    }

    private PlaybookEvalResult BuildResult(int actionVersion, int goldenCaseTotal, IReadOnlyList<PlaybookEvalCaseResult> caseResults)
    {
        var baselinePass = caseResults.Count(static caseResult => caseResult.BaselinePass);
        var candidatePass = caseResults.Count(static caseResult => caseResult.CandidatePass);
        var regressed = caseResults.Count(static caseResult => caseResult.Regressed);
        var improved = caseResults.Count(static caseResult => !caseResult.BaselinePass && caseResult.CandidatePass);

        // Passed requires at least one golden case AND zero regressions (no-regression is unprovable with zero cases).
        var passed = caseResults.Count > 0 && regressed == 0;

        return new PlaybookEvalResult(passed,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            actionVersion,
            _options.ModelName,
            caseResults.Count,
            goldenCaseTotal,
            baselinePass,
            candidatePass,
            regressed,
            improved,
            caseResults);
    }

    private PlaybookEvalResult BuildEmptyResult(int actionVersion)
    {
        return new PlaybookEvalResult(Passed: false,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            actionVersion,
            _options.ModelName,
            GoldenCaseCount: 0,
            GoldenCaseTotal: 0,
            BaselinePassCount: 0,
            CandidatePassCount: 0,
            RegressedCaseCount: 0,
            ImprovedCaseCount: 0,
            Cases: []);
    }

    private async Task<PlaybookEvalOutcome> PersistAsync(Guid agentId, Guid actionId, PlaybookEvalResult result, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, PlaybookEvalResult.SerializerOptions);

        // Recording persists the JSON on the action under the ownership guard and yields the updated record, which we
        // thread out via the outcome so the endpoint maps the response directly with no second, unscoped fetch.
        var updated = await _playbookActionService.RecordEvalResultAsync(agentId, actionId, json, cancellationToken).ConfigureAwait(false);
        return new PlaybookEvalOutcome(true, result, updated);
    }

    // Positional record: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults).
    private sealed record InputTurn(string? Role, string? Text);
}
