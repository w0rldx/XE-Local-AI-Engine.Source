namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for playbook action data.
/// </summary>
public sealed class PlaybookActionStore : IPlaybookActionStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PlaybookActionStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<PlaybookActionRecord> AddAsync(PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new PlaybookAction
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = input.AgentDefinitionId,
            State = (int)input.State,
            Source = (int)input.Source,
            MemoryScope = (int?)input.MemoryScope,
            TriggerCondition = EncodeOptional(input.TriggerCondition),
            Behavior = Encoding.UTF8.GetBytes(input.Behavior),
            Scope = input.Scope,
            SourceFeedbackIds = EncodeFeedbackIds(input.SourceFeedbackIds),
            Confidence = input.Confidence,
            EvalResult = input.EvalResult,
            Priority = input.Priority,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            // Cohort-monitoring clock: a create-as-Enabled action is stamped now, otherwise it carries what the caller supplied (null for a fresh
            // Suggested/Disabled action). Stamping here and in UpdateAsync makes the store the single source of truth — every Enabled action gets an EnabledAtUtc.
            EnabledAtUtc = input.State == PlaybookActionState.Enabled ? now : input.EnabledAtUtc
        };

        _ = _dbContext.PlaybookActions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRecord(entity);
    }

    public async Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Behavior/TriggerCondition on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.PlaybookActions
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        // Only Behavior (injected text), Priority (injection order) and State (injection membership) change what the resolver folds into the prompt.
        // Scope/TriggerCondition/Source are not injected, so editing them alone must not bump Version.
        var configChanged = !string.Equals(Decode(entity.Behavior), input.Behavior, StringComparison.Ordinal)
                            || entity.Priority != input.Priority
                            || entity.State != (int)input.State;

        // Cohort-monitoring clock: detect a transition INTO Enabled from the pre-mutation state, before the assignment below. The eval-gated promote and a manual
        // Disabled->Enabled toggle both stamp it; anything staying out of Enabled carries the caller's value through, and disabling never clears it.
        var enabledNow = (int)PlaybookActionState.Enabled;
        var transitioningIntoEnabled = entity.State != enabledNow && input.State == PlaybookActionState.Enabled;

        // AgentDefinitionId is deliberately NOT reassigned: an action never moves agents. The application service already rejects a cross-agent update, and
        // leaving the FK column untouched is defense-in-depth even if a future caller bypasses that guard.
        entity.State = (int)input.State;
        entity.Source = (int)input.Source;
        // MemoryScope is non-injected metadata (like Scope/Source/Confidence), so it is excluded from configChanged
        // above and never bumps Version on its own; it is simply carried through.
        entity.MemoryScope = (int?)input.MemoryScope;
        entity.TriggerCondition = EncodeOptional(input.TriggerCondition);
        entity.Behavior = Encoding.UTF8.GetBytes(input.Behavior);
        entity.Scope = input.Scope;
        // Provenance/confidence are not injected into the prompt, so updating them never bumps Version (mirrors
        // Scope/TriggerCondition). The review paths (promote/reject/edit) carry the existing values through.
        entity.SourceFeedbackIds = EncodeFeedbackIds(input.SourceFeedbackIds);
        entity.Confidence = input.Confidence;
        // EvalResult is deliberately excluded from configChanged: it is not injected into the prompt, so recording an
        // eval (or clearing it on edit) must never bump Version (mirrors SourceFeedbackIds/Confidence above).
        entity.EvalResult = input.EvalResult;
        entity.Priority = input.Priority;
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // EnabledAtUtc is a pure timestamp, so like EvalResult it is excluded from configChanged and never bumps Version on its own; a co-occurring State change
        // bumps it on its own merit. Stamped now on a transition into Enabled, otherwise carried through so an edit, eval record or reject preserves the instant.
        entity.EnabledAtUtc = transitioningIntoEnabled ? now : input.EnabledAtUtc;
        entity.UpdatedAtUtc = now;

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRecord(entity);
    }

    public async Task<PlaybookPromotionCommit> PromoteSuggestedIfCurrentAsync(Guid id,
        int expectedVersion,
        int maxEnabledActions,
        string? evalResult,
        CancellationToken cancellationToken = default)
    {
        // Serialize the version/state guard, the cap re-check and the Enabled write into one transaction so a concurrent edit or promote cannot slip between the
        // checks and the write. Any early return disposes the transaction, rolling back with nothing written.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var entity = await _dbContext.PlaybookActions
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken);
        if (entity is null)
        {
            return new PlaybookPromotionCommit { Status = PlaybookPromotionCommitStatus.NotFound, Record = null };
        }

        // Optimistic-concurrency guard: the row must still be the exact snapshot the caller validated. A concurrent UpdateSuggestedAsync bumps Version and clears
        // the eval, a concurrent promote moves State off Suggested — either way the eval evidence no longer proves this content, so refuse rather than enable on it.
        if (entity.Version != expectedVersion || entity.State != (int)PlaybookActionState.Suggested)
        {
            return new PlaybookPromotionCommit { Status = PlaybookPromotionCommitStatus.VersionConflict, Record = null };
        }

        // Cap re-check adjacent to the write, in the same transaction: two concurrent promotes cannot both read a
        // below-cap count and both enable, because the count read and the Enabled write commit atomically.
        var enabled = (int)PlaybookActionState.Enabled;
        var enabledCount = await _dbContext.PlaybookActions
                                           .CountAsync(action => action.AgentDefinitionId == entity.AgentDefinitionId && action.State == enabled, cancellationToken);
        if (enabledCount >= maxEnabledActions)
        {
            return new PlaybookPromotionCommit { Status = PlaybookPromotionCommitStatus.CapReached, Record = null };
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entity.State = enabled;
        entity.EvalResult = evalResult;
        entity.EnabledAtUtc = now;
        entity.UpdatedAtUtc = now;
        // State transition (Suggested -> Enabled) is config-affecting, so Version bumps — mirrors UpdateAsync's rule.
        entity.Version++;

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PlaybookPromotionCommit { Status = PlaybookPromotionCommitStatus.Committed, Record = ToRecord(entity) };
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlaybookActions
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.PlaybookActions.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlaybookActions
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.PlaybookActions
                                       .AsNoTracking()
                                       .Where(action => action.AgentDefinitionId == agentDefinitionId)
                                       .OrderBy(action => action.Priority)
                                       .ThenBy(action => action.CreatedAtUtc)
                                       .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<PlaybookActionRecord>> ListEnabledByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var enabled = (int)PlaybookActionState.Enabled;

        var entities = await _dbContext.PlaybookActions
                                       .AsNoTracking()
                                       .Where(action => action.AgentDefinitionId == agentDefinitionId && action.State == enabled)
                                       .OrderBy(action => action.Priority)
                                       .ThenBy(action => action.CreatedAtUtc)
                                       .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToArray();
    }

    private static PlaybookActionRecord ToRecord(PlaybookAction entity)
    {
        return new PlaybookActionRecord
        {
            Id = entity.Id,
            AgentDefinitionId = entity.AgentDefinitionId,
            State = (PlaybookActionState)entity.State,
            Source = (PlaybookActionSource)entity.Source,
            TriggerCondition = entity.TriggerCondition is null ? null : Decode(entity.TriggerCondition),
            Behavior = Decode(entity.Behavior),
            Scope = entity.Scope,
            Priority = entity.Priority,
            Version = entity.Version,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            SourceFeedbackIds = DecodeFeedbackIds(entity.SourceFeedbackIds),
            Confidence = entity.Confidence,
            EvalResult = entity.EvalResult,
            EnabledAtUtc = entity.EnabledAtUtc,
            MemoryScope = (MemoryScope?)entity.MemoryScope
        };
    }

    private static byte[]? EncodeOptional(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string? EncodeFeedbackIds(IReadOnlyList<Guid>? feedbackIds)
    {
        // Provenance ids are stored as a JSON array (plaintext — ids only, not sensitive). Null stays null so a
        // manual action carries no provenance column.
        return feedbackIds is null ? null : JsonSerializer.Serialize(feedbackIds);
    }

    private static IReadOnlyList<Guid>? DecodeFeedbackIds(string? json)
    {
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Guid[]>(json);
    }
}
