namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Rule-set CRUD. Its own partial rather than more of <c>DevWorkflowStore.Crud.cs</c>: a rule set is a third
///     aggregate root, tied neither to a work item nor to a run, and the file split here already groups by that.
/// </summary>
internal sealed partial class DevWorkflowStore
{
    public async Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.Body, nameof(command.Body));
        EnsureNotBlank(command.ScopeJson, nameof(command.ScopeJson));

        var body = Utf8(command.Body);
        var now = Now();
        var ruleSet = new DevWorkflowRuleSet
        {
            Id = command.RuleSetId,
            Name = command.Name,
            Description = command.Description,
            ScopeJson = command.ScopeJson,
            Enabled = command.Enabled,
            Body = body,

            // Hashed here, in the same save as the bytes, so a recorded hash can never name text this row does not hold.
            ContentSha256 = HashPayload(body),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.DevWorkflowRuleSets.Add(ruleSet);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw new DevWorkflowConcurrencyException($"A development workflow rule set already exists for id '{command.RuleSetId}'.", exception);
        }

        return RuleSetSnapshot(ruleSet);
    }

    public async Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.Body, nameof(command.Body));
        EnsureNotBlank(command.ScopeJson, nameof(command.ScopeJson));

        var ruleSet = await LoadRuleSetAsync(command.RuleSetId, cancellationToken).ConfigureAwait(false);
        if (ruleSet.Version != command.ExpectedVersion)
        {
            throw new DevWorkflowConcurrencyException($"The rule set version is stale (expected {command.ExpectedVersion}, current {ruleSet.Version}).");
        }

        var body = Utf8(command.Body);
        ruleSet.Name = command.Name;
        ruleSet.Description = command.Description;
        ruleSet.ScopeJson = command.ScopeJson;
        ruleSet.Enabled = command.Enabled;
        ruleSet.Body = body;
        ruleSet.ContentSha256 = HashPayload(body);
        ruleSet.Version++;
        ruleSet.UpdatedAtUtc = Now();
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Cleared, or the rule set stays tracked as Modified and the next write in this scope re-submits an edit
            // that has already been refused once.
            _dbContext.ChangeTracker.Clear();
            throw new DevWorkflowConcurrencyException("The rule set was changed by another writer before this edit could be written, so its version is stale.",
                exception);
        }

        return RuleSetSnapshot(ruleSet);
    }

    public async Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default) =>
        await SummariesAsync(_dbContext.DevWorkflowRuleSets.AsNoTracking(), cancellationToken).ConfigureAwait(false);

    public async Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        var ruleSet = await _dbContext.DevWorkflowRuleSets.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.Id == ruleSetId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new DevWorkflowNotFoundException($"Development workflow rule set '{ruleSetId}' was not found.");
        return RuleSetSnapshot(ruleSet);
    }

    public async Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        var removed = await _dbContext.DevWorkflowRuleSets.Where(entity => entity.Id == ruleSetId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed == 0)
        {
            throw new DevWorkflowNotFoundException($"Development workflow rule set '{ruleSetId}' was not found.");
        }
    }

    /// <summary>
    ///     Bodies INCLUDED, unlike the list feed: the resolver snapshots the text of every rule set it matches onto the
    ///     node run, so it needs the document and not merely its hash. Ordered by name, which is also the order every
    ///     matching set is injected in.
    /// </summary>
    public async Task<IReadOnlyList<DevWorkflowRuleSetSnapshot>> ListEnabledRuleSetsAsync(CancellationToken cancellationToken = default)
    {
        var ruleSets = await _dbContext.DevWorkflowRuleSets.AsNoTracking()
                                       .Where(entity => entity.Enabled)
                                       .OrderBy(entity => entity.Name)
                                       .ThenBy(entity => entity.Id)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return [.. ruleSets.Select(RuleSetSnapshot)];
    }

    /// <summary>
    ///     Projected server-side without <c>body</c>, so no rule-set blob is decrypted to draw the list. Ordered by
    ///     name, which is also the order every matching set is injected in.
    /// </summary>
    private static async Task<IReadOnlyList<DevWorkflowRuleSetSummary>> SummariesAsync(IQueryable<DevWorkflowRuleSet> query, CancellationToken cancellationToken) =>
        await query.OrderBy(entity => entity.Name)
                   .ThenBy(entity => entity.Id)
                   .Select(entity => new DevWorkflowRuleSetSummary(entity.Id,
                       entity.Name,
                       entity.Description,
                       entity.ScopeJson,
                       entity.Enabled,
                       entity.ContentSha256,
                       entity.Version,
                       entity.CreatedAtUtc,
                       entity.UpdatedAtUtc))
                   .ToListAsync(cancellationToken)
                   .ConfigureAwait(false);

    private async Task<DevWorkflowRuleSet> LoadRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken) =>
        await _dbContext.DevWorkflowRuleSets.SingleOrDefaultAsync(entity => entity.Id == ruleSetId, cancellationToken).ConfigureAwait(false)
        ?? throw new DevWorkflowNotFoundException($"Development workflow rule set '{ruleSetId}' was not found.");

    private static DevWorkflowRuleSetSnapshot RuleSetSnapshot(DevWorkflowRuleSet ruleSet) =>
        new(ruleSet.Id,
            ruleSet.Name,
            ruleSet.Description,
            ruleSet.ScopeJson,
            ruleSet.Enabled,
            Text(ruleSet.Body),
            ruleSet.ContentSha256,
            ruleSet.Version,
            ruleSet.CreatedAtUtc,
            ruleSet.UpdatedAtUtc);
}
