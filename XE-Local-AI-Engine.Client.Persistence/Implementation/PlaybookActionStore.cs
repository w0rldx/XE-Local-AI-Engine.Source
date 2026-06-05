namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for playbook action data.
/// </summary>
public sealed class PlaybookActionStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IPlaybookActionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
            // P5 cohort-monitoring clock: a create-as-Enabled action gets its clock stamped now; otherwise it carries
            // whatever the caller supplied (null for a fresh Suggested/Disabled action). Centralizing the stamp here and
            // in UpdateAsync makes the store the single source of truth — every Enabled action gets an EnabledAtUtc.
            EnabledAtUtc = input.State == PlaybookActionState.Enabled ? now : input.EnabledAtUtc
        };

        _ = _dbContext.PlaybookActions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Behavior/TriggerCondition on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.PlaybookActions
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // Only Behavior (injected text), Priority (injection order) and State (injection membership) change what the
        // resolver folds into the prompt; Scope/TriggerCondition/Source are not injected in P1, so editing them alone
        // must not bump Version.
        var configChanged = !string.Equals(Decode(entity.Behavior), input.Behavior, StringComparison.Ordinal)
                            || entity.Priority != input.Priority
                            || entity.State != (int)input.State;

        // P5 cohort-monitoring clock: detect a transition INTO Enabled (read the pre-mutation state, before the
        // assignment below). The eval-gated promote (Suggested->Enabled) and a manual Disabled->Enabled toggle both
        // stamp the clock; an edit/eval-record/reject that stays out of Enabled carries the caller's value through.
        // Never cleared on disable — the last-enabled instant is preserved.
        var enabledNow = (int)PlaybookActionState.Enabled;
        var transitioningIntoEnabled = entity.State != enabledNow && input.State == PlaybookActionState.Enabled;

        // AgentDefinitionId is deliberately NOT reassigned: an action never moves agents. The application service
        // already rejects a cross-agent update, and leaving the FK column untouched is defense-in-depth even if a
        // future caller bypasses that guard.
        entity.State = (int)input.State;
        entity.Source = (int)input.Source;
        entity.TriggerCondition = EncodeOptional(input.TriggerCondition);
        entity.Behavior = Encoding.UTF8.GetBytes(input.Behavior);
        entity.Scope = input.Scope;
        // Provenance/confidence are not injected into the prompt, so updating them never bumps Version (mirrors
        // Scope/TriggerCondition). The P3 review paths (promote/reject/edit) carry the existing values through.
        entity.SourceFeedbackIds = EncodeFeedbackIds(input.SourceFeedbackIds);
        entity.Confidence = input.Confidence;
        // EvalResult is deliberately excluded from configChanged: it is not injected into the prompt, so recording an
        // eval (or clearing it on edit) must never bump Version (mirrors SourceFeedbackIds/Confidence above).
        entity.EvalResult = input.EvalResult;
        entity.Priority = input.Priority;
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // EnabledAtUtc is a pure timestamp, so like EvalResult it is excluded from configChanged and never bumps
        // Version on its own; a co-occurring State change bumps Version on its own merit. Stamp now when transitioning
        // into Enabled, otherwise carry the caller's value through so an edit/eval-record/reject preserves the
        // last-enabled instant.
        entity.EnabledAtUtc = transitioningIntoEnabled ? now : input.EnabledAtUtc;
        entity.UpdatedAtUtc = now;

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlaybookActions
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.PlaybookActions.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlaybookActions
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(action => action.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.PlaybookActions
                                       .AsNoTracking()
                                       .Where(action => action.AgentDefinitionId == agentDefinitionId)
                                       .OrderBy(action => action.Priority)
                                       .ThenBy(action => action.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

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
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static PlaybookActionRecord ToRecord(PlaybookAction entity)
    {
        return new PlaybookActionRecord(entity.Id,
            entity.AgentDefinitionId,
            (PlaybookActionState)entity.State,
            (PlaybookActionSource)entity.Source,
            entity.TriggerCondition is null ? null : Decode(entity.TriggerCondition),
            Decode(entity.Behavior),
            entity.Scope,
            entity.Priority,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            DecodeFeedbackIds(entity.SourceFeedbackIds),
            entity.Confidence,
            entity.EvalResult,
            entity.EnabledAtUtc);
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
