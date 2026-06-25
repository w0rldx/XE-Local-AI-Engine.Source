namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the agent skill library.
/// </summary>
public sealed class AgentSkillStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentSkillStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new AgentSkill
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = Encoding.UTF8.GetBytes(input.Description),
            Body = Encoding.UTF8.GetBytes(input.Body),
            Enabled = input.Enabled,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.AgentSkills.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Description/Body on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.AgentSkills
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // Name, Description and Body are the content the model sees / loads, so they drive the config hash and bump
        // Version. The Enabled toggle only gates resolution (already covered by resolved-set membership in the hash), so
        // toggling it alone must NOT bump Version — mirrors the PlaybookAction/AgentDefinition version rule.
        var configChanged = !string.Equals(entity.Name, input.Name, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.Description), input.Description, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.Body), input.Body, StringComparison.Ordinal);

        entity.Name = input.Name;
        entity.Description = Encoding.UTF8.GetBytes(input.Description);
        entity.Body = Encoding.UTF8.GetBytes(input.Body);
        entity.Enabled = input.Enabled;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentSkills
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.AgentSkills.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentSkills
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.AgentSkills
                                       .AsNoTracking()
                                       .OrderBy(skill => skill.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<AgentSkillRecord>> ListEnabledByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        // Materialize the requested ids into a hash set so the EF Contains translates to a single IN (...) query and
        // duplicate ids in the picklist collapse. Filter to Enabled server-side; missing/disabled ids simply do not
        // appear in the result (the resolver drops + logs them).
        var idSet = ids.ToHashSet();

        var entities = await _dbContext.AgentSkills
                                       .AsNoTracking()
                                       .Where(skill => skill.Enabled && idSet.Contains(skill.Id))
                                       .OrderBy(skill => skill.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static AgentSkillRecord ToRecord(AgentSkill entity)
    {
        return new AgentSkillRecord(entity.Id,
            entity.Name,
            Decode(entity.Description),
            Decode(entity.Body),
            entity.Enabled,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }
}
