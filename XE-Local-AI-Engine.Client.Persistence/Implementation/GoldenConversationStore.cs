namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for golden conversation data.
/// </summary>
public sealed class GoldenConversationStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IGoldenConversationStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<GoldenConversationRecord> AddAsync(GoldenConversationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new GoldenConversation
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = input.AgentDefinitionId,
            Title = input.Title,
            InputTurns = Encoding.UTF8.GetBytes(input.InputTurns),
            Assertion = EncodeOptional(input.Assertion),
            Rubric = EncodeOptional(input.Rubric),
            Enabled = input.Enabled,
            Source = input.Source,
            SourceMessageId = input.SourceMessageId,
            SourceConversationId = input.SourceConversationId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.GoldenConversations.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.GoldenConversations
                                       .AsNoTracking()
                                       .Where(golden => golden.AgentDefinitionId == agentDefinitionId)
                                       .OrderBy(golden => golden.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<GoldenConversationRecord>> ListEnabledByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.GoldenConversations
                                       .AsNoTracking()
                                       .Where(golden => golden.AgentDefinitionId == agentDefinitionId && golden.Enabled)
                                       .OrderBy(golden => golden.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<GoldenConversationRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.GoldenConversations
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(golden => golden.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<GoldenConversationRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.GoldenConversations
                                     .FirstOrDefaultAsync(golden => golden.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.Enabled = enabled;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<Guid>> ListSourceMessageIdsByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.GoldenConversations
                               .AsNoTracking()
                               .Where(golden => golden.AgentDefinitionId == agentDefinitionId && golden.SourceMessageId != null)
                               .Select(golden => golden.SourceMessageId!.Value)
                               .ToArrayAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.GoldenConversations
                                     .FirstOrDefaultAsync(golden => golden.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.GoldenConversations.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static GoldenConversationRecord ToRecord(GoldenConversation entity)
    {
        return new GoldenConversationRecord(entity.Id,
            entity.AgentDefinitionId,
            entity.Title,
            Decode(entity.InputTurns),
            entity.Assertion is null ? null : Decode(entity.Assertion),
            entity.Rubric is null ? null : Decode(entity.Rubric),
            entity.Enabled,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.Source,
            entity.SourceMessageId,
            entity.SourceConversationId);
    }

    private static byte[]? EncodeOptional(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }
}
