namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for canvas (Open Canvas preview) workflows.
/// </summary>
public sealed class CanvasWorkflowStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ICanvasWorkflowStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<CanvasWorkflowRecord> AddAsync(CanvasWorkflowInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new CanvasWorkflow
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            GraphJson = Encoding.UTF8.GetBytes(input.GraphJson),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.CanvasWorkflows.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<CanvasWorkflowUpdateResult> UpdateAsync(Guid id,
        int expectedVersion,
        CanvasWorkflowInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted GraphJson on load.
        var entity = await _dbContext.CanvasWorkflows
                                     .FirstOrDefaultAsync(workflow => workflow.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return CanvasWorkflowUpdateResult.NotFound();
        }

        // Optimistic concurrency: a stale expected version means another writer advanced the row first — reject as a
        // conflict (the endpoint surfaces this as a 409) rather than silently overwriting.
        if (entity.Version != expectedVersion)
        {
            return CanvasWorkflowUpdateResult.Conflict();
        }

        entity.Name = input.Name;
        entity.GraphJson = Encoding.UTF8.GetBytes(input.GraphJson);
        entity.Version++;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return CanvasWorkflowUpdateResult.Updated(ToRecord(entity));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CanvasWorkflows
                                     .FirstOrDefaultAsync(workflow => workflow.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.CanvasWorkflows.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<CanvasWorkflowRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CanvasWorkflows
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(workflow => workflow.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<CanvasWorkflowRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Summaries only — project the plaintext columns and never load the encrypted graph blob.
        var summaries = await _dbContext.CanvasWorkflows
                                        .AsNoTracking()
                                        .OrderBy(workflow => workflow.CreatedAtUtc)
                                        .Select(workflow => new CanvasWorkflowRecord(workflow.Id,
                                            workflow.Name,
                                            null,
                                            workflow.Version,
                                            workflow.CreatedAtUtc,
                                            workflow.UpdatedAtUtc))
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);

        return summaries;
    }

    private static CanvasWorkflowRecord ToRecord(CanvasWorkflow entity)
    {
        return new CanvasWorkflowRecord(entity.Id,
            entity.Name,
            Encoding.UTF8.GetString(entity.GraphJson),
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }
}
