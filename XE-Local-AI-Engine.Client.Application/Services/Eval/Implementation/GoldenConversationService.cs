namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Default <see cref="IGoldenConversationService" /> for manual golden-case authoring and harvested-candidate
///     staging. Validates a golden case before persisting (non-blank Title, existing owning agent, non-empty
///     InputTurns, at least one of {Assertion, Rubric}, boundary length caps) and applies the same IDOR-safe ownership
///     guard to delete/approve as the manual-authoring and analysis-review paths. The manual create path pins Source=Manual; the harvested
///     create path pins Source=Harvested + stages the case inert. Reuses <see cref="PlaybookActionValidationException" />
///     so callers map a validation failure the same way for both playbook surfaces.
/// </summary>
internal sealed class GoldenConversationService(
    IGoldenConversationStore store,
    IAgentDefinitionStore agentDefinitionStore) : IGoldenConversationService
{
    // Boundary length caps (mirror the PlaybookAction free-text 20_000 cap). The Title is a short operator label, the
    // serialized turns can be larger (multi-turn conversations), and the assertion/rubric hold scoring text.
    private const int MaxTitleLength = 200;
    private const int MaxInputTurnsLength = 50_000;
    private const int MaxRubricLength = 20_000;
    private const int MaxAssertionLength = 20_000;
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));

    private readonly IGoldenConversationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<GoldenConversationRecord> CreateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);

        // A manual create never produces harvested provenance: pin Source=Manual regardless of the input so the manual
        // path always stamps Manual (the harvested staging path is the only producer of Harvested rows).
        var storeInput = new GoldenConversationInput(input.AgentDefinitionId,
            input.Title,
            input.InputTurns,
            input.Assertion,
            input.Rubric,
            input.Enabled);

        return await _store.AddAsync(storeInput, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GoldenConversationRecord> CreateHarvestedAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);

        // Provenance is required for a harvested candidate (dedup + review trace back to the thumbs-up message).
        if (input.SourceMessageId is null || input.SourceConversationId is null)
        {
            throw new PlaybookActionValidationException("A harvested golden case requires both SourceMessageId and SourceConversationId.");
        }

        // Stage every harvested candidate inert regardless of the input's Enabled flag: the operator approves it into the
        // active set later, so it stays out of eval runs until then (the Enabled==true runner filter).
        var storeInput = new GoldenConversationInput(input.AgentDefinitionId,
            input.Title,
            input.InputTurns,
            input.Assertion,
            input.Rubric,
            Enabled: false,
            GoldenConversationSource.Harvested,
            input.SourceMessageId,
            input.SourceConversationId);

        return await _store.AddAsync(storeInput, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        return _store.ListByAgentAsync(agentDefinitionId, cancellationToken);
    }

    public async Task<GoldenConversationRecord?> ApproveHarvestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Ownership + staging guard: only promote a harvested, currently-disabled case that belongs to the route agent,
        // so one agent's route cannot approve another agent's case (IDOR), and a manual or already-active case is never
        // flipped here. Any miss returns null so the endpoint maps it to 404.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null
            || existing.AgentDefinitionId != agentDefinitionId
            || existing.Source != GoldenConversationSource.Harvested
            || existing.Enabled)
        {
            return null;
        }

        return await _store.SetEnabledAsync(id, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Ownership guard: only delete the case when it belongs to the route agent, so one agent's golden route cannot
        // delete another agent's case (IDOR).
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != agentDefinitionId)
        {
            return false;
        }

        return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    // Shared boundary validation for both create paths (DRY — keeps manual and harvested creates identical): non-blank
    // Title/InputTurns, at least one scoring signal, the four length caps, and an existing owning agent (the FK demands
    // it; reject up front rather than surface a downstream constraint failure).
    private async Task ValidateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new PlaybookActionValidationException("Title is required.");
        }

        if (string.IsNullOrWhiteSpace(input.InputTurns))
        {
            throw new PlaybookActionValidationException("InputTurns is required.");
        }

        // judge path: a golden case must carry at least one scoring signal — an assertion (deterministic) and/or a rubric
        // (judge). A case with neither cannot be scored, so it is rejected at the boundary.
        if (string.IsNullOrWhiteSpace(input.Assertion) && string.IsNullOrWhiteSpace(input.Rubric))
        {
            throw new PlaybookActionValidationException("A golden case must carry at least one of an Assertion or a Rubric.");
        }

        // Reject over-long fields at the boundary (never trust client-supplied lengths) — bounds the encrypted payload
        // and matches the React caps so both surfaces reject the same input.
        if (input.Title.Length > MaxTitleLength)
        {
            throw new PlaybookActionValidationException($"Title must be {MaxTitleLength} characters or fewer.");
        }

        if (input.InputTurns.Length > MaxInputTurnsLength)
        {
            throw new PlaybookActionValidationException($"InputTurns must be {MaxInputTurnsLength} characters or fewer.");
        }

        if (input.Assertion is { Length: > MaxAssertionLength })
        {
            throw new PlaybookActionValidationException($"Assertion must be {MaxAssertionLength} characters or fewer.");
        }

        if (input.Rubric is { Length: > MaxRubricLength })
        {
            throw new PlaybookActionValidationException($"Rubric must be {MaxRubricLength} characters or fewer.");
        }

        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }
    }
}
