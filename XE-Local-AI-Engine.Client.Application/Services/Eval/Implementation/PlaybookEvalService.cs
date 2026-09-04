namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Eval;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

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
    IEvalModelIdentityResolver modelIdentityResolver,
    TimeProvider timeProvider,
    IOptions<PlaybookEvalOptions> options,
    ILogger<PlaybookEvalService> logger) : IPlaybookEvalService
{
    /// <summary>
    ///     <see cref="PlaybookEvalCaseResult.ScoredBy" /> value for a golden case whose stored input turns are unusable
    ///     (malformed/empty/unknown-role): the case is recorded as an explicit failed result with no model call, so it
    ///     never silently evaluates the system prompt alone and passes.
    /// </summary>
    internal const string InvalidInputScoredBy = "invalid-input";

    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IPlaybookEvalAgentRunner _evalAgentRunner = evalAgentRunner ?? throw new ArgumentNullException(nameof(evalAgentRunner));
    private readonly IPlaybookEvalJudge _evalJudge = evalJudge ?? throw new ArgumentNullException(nameof(evalJudge));
    private readonly IGoldenConversationStore _goldenConversationStore = goldenConversationStore ?? throw new ArgumentNullException(nameof(goldenConversationStore));
    private readonly ILogger<PlaybookEvalService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly PlaybookEvalOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly IEvalModelIdentityResolver _modelIdentityResolver = modelIdentityResolver ?? throw new ArgumentNullException(nameof(modelIdentityResolver));
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));
    private readonly IPlaybookActionStore _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PlaybookEvalOutcome> RunEvalAsync(Guid agentId, Guid actionId, CancellationToken cancellationToken = default)
    {
        // Same ownership + Suggested + Analysis guard as the review paths — a missing/cross-agent/non-pending action
        // surfaces ActionFound == false so the endpoint 404s.
        var suggested = await _playbookActionService.LoadPendingSuggestionAsync(agentId, actionId, cancellationToken).ConfigureAwait(false);
        if (suggested is null)
        {
            return new PlaybookEvalOutcome(ActionFound: false, Result: null);
        }

        var agent = await _agentDefinitionStore.GetByIdAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return new PlaybookEvalOutcome(ActionFound: false, Result: null);
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

        // Fingerprint the behaviour-affecting inputs over the FULL enabled golden set (before any per-run cap) so the
        // promote gate can detect a base-instruction / sibling-action / golden-set / model change after this eval ran.
        // The model identity (weight digest) is folded in alongside the name so a same-name weight swap between eval and
        // promote invalidates the fingerprint; an unresolvable identity records the explicit unverified sentinel.
        var modelIdentity = await _modelIdentityResolver.ResolveAsync(_options.ModelName, cancellationToken).ConfigureAwait(false);
        var fingerprint = PlaybookEvalFingerprint.Compute(suggested.Id,
            suggested.Version,
            agent.Instructions,
            enabled,
            goldenCases,
            _options.ModelName,
            modelIdentity.Token);

        // Empty golden set never passes (no-regression is unprovable with zero cases) — persist a failing result so the
        // gap is visible (promote stays blocked) rather than silently waved through.
        if (goldenCases.Count == 0)
        {
            _logger.LogWarning("Eval for agent {AgentId} action {ActionId} has no golden cases; recording a failing result (needs golden cases).", agentId, actionId);
            return await PersistAsync(agentId, actionId, BuildEmptyResult(suggested.Version, fingerprint), cancellationToken).ConfigureAwait(false);
        }

        if (goldenCases.Count > _options.MaxGoldenCases)
        {
            // Cap is a cost guard, NOT a pass shortcut: the run stays INCOMPLETE (GoldenCaseCount < GoldenCaseTotal) and
            // the promote gate refuses to authorize it. An operator raises MaxGoldenCases to run a complete eval.
            _logger.LogWarning("Eval for agent {AgentId} has {Count} golden cases; evaluating only {Max} (the run will be marked incomplete and cannot authorize promotion).", agentId,
                goldenCases.Count, _options.MaxGoldenCases);
            goldenCases = [.. goldenCases.Take(_options.MaxGoldenCases)];
        }

        // Route the configured eval model to the runtime that serves it (persisted map, else the configured default
        // provider = ollama — an un-repointed model behaves exactly as before). Node-local only — never the
        // shared/cloud singleton.
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

        var result = BuildResult(suggested.Version, goldenCaseTotal, caseResults, fingerprint);
        return await PersistAsync(agentId, actionId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybookEvalCaseResult> ScoreCaseAsync(GoldenConversationRecord goldenCase,
        string baselinePrompt,
        string candidatePrompt,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        // Unusable stored turns (malformed JSON, no turns, an unknown role, or a blank-text turn) cannot demonstrate
        // quality — record an EXPLICIT failed case (no model call) rather than silently evaluating the system prompt
        // alone, which would let a broken case pass. Validation blocks these at create/update; this covers legacy rows.
        if (!GoldenInputTurns.TryParse(goldenCase.InputTurns, out var turns, out var turnsError))
        {
            _logger.LogWarning("Golden case {GoldenCaseId} has unusable input turns ({Reason}); recording an explicit failed case.", goldenCase.Id, turnsError);
            return new PlaybookEvalCaseResult(goldenCase.Id, InvalidInputScoredBy, BaselinePass: false, CandidatePass: false, Regressed: false);
        }

        // Both arms run at the SAME configured effort (null by default, which is the pre-existing behaviour): the gate
        // measures the injected prompt, so an effort difference between the two would confound it.
        var baselineText = await _evalAgentRunner.RunAsync(chatClient, baselinePrompt, turns, _options.ReasoningEffort, cancellationToken).ConfigureAwait(false);
        var candidateText = await _evalAgentRunner.RunAsync(chatClient, candidatePrompt, turns, _options.ReasoningEffort, cancellationToken).ConfigureAwait(false);

        var baselineScore = await _evalJudge.ScoreAsync(goldenCase, baselineText, chatClient, cancellationToken).ConfigureAwait(false);
        var candidateScore = await _evalJudge.ScoreAsync(goldenCase, candidateText, chatClient, cancellationToken).ConfigureAwait(false);

        // Regression criterion: a case the baseline passed and the candidate fails is a regression.
        var regressed = baselineScore.Pass && !candidateScore.Pass;

        // Record the CANDIDATE's scoring path — the candidate is the thing under evaluation. (Baseline and candidate
        // agree today since both score the same golden case, but the candidate is the correct field to attribute.)
        return new PlaybookEvalCaseResult(goldenCase.Id, candidateScore.ScoredBy, baselineScore.Pass, candidateScore.Pass, regressed);
    }

    private PlaybookEvalResult BuildResult(int actionVersion, int goldenCaseTotal, IReadOnlyList<PlaybookEvalCaseResult> caseResults, string fingerprint)
    {
        var baselinePass = caseResults.Count(static caseResult => caseResult.BaselinePass);
        var candidatePass = caseResults.Count(static caseResult => caseResult.CandidatePass);
        var regressed = caseResults.Count(static caseResult => caseResult.Regressed);
        var improved = caseResults.Count(static caseResult => !caseResult.BaselinePass && caseResult.CandidatePass);

        // Passed requires two independent signals, BOTH surfaced honestly via the counts below:
        //   1. No-regression   (RegressedCaseCount == 0)      — no prior-good case broke.
        //   2. Absolute floor  (CandidatePassCount  > 0)      — at least one case actually passed.
        // The absolute floor closes a gap: a run where EVERY baseline and candidate case fails has zero regressions but
        // proves nothing, and must NOT pass on the no-regression signal alone. Also requires at least one evaluated case
        // (no-regression is unprovable with zero cases). Passed is a subset property; completeness
        // (GoldenCaseCount == GoldenCaseTotal) is enforced separately by the promote gate, so a subset "pass" of a
        // truncated run still cannot authorize promotion.
        var passed = caseResults.Count > 0 && regressed == 0 && candidatePass > 0;

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
            caseResults,
            fingerprint);
    }

    private PlaybookEvalResult BuildEmptyResult(int actionVersion, string fingerprint)
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
            [],
            fingerprint);
    }

    private async Task<PlaybookEvalOutcome> PersistAsync(Guid agentId, Guid actionId, PlaybookEvalResult result, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, PlaybookEvalResult.SerializerOptions);

        // Recording persists the JSON on the action under the ownership guard and yields the updated record, which we
        // thread out via the outcome so the endpoint maps the response directly with no second, unscoped fetch.
        var updated = await _playbookActionService.RecordEvalResultAsync(agentId, actionId, json, cancellationToken).ConfigureAwait(false);
        return new PlaybookEvalOutcome(ActionFound: true, result, updated);
    }
}
