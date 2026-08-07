namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for node selected folder data.
/// </summary>
public sealed class NodeSelectedFolderStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : INodeSelectedFolderStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<SelectedFolderRecord> AddAsync(string folderAlias, string hostPath, SelectedFolderMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var entity = new NodeSelectedFolder
        {
            Id = Guid.NewGuid(),
            Alias = folderAlias,
            HostPath = Encoding.UTF8.GetBytes(hostPath),
            Mode = mode,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.SelectedFolders.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<SelectedFolderRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.SelectedFolders
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(folder => folder.Id == id && folder.RevokedAtUtc == null, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderAlias);

        var entity = await _dbContext.SelectedFolders
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(folder => folder.Alias == folderAlias && folder.RevokedAtUtc == null, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.SelectedFolders
                                       .AsNoTracking()
                                       .Where(folder => folder.RevokedAtUtc == null)
                                       .OrderBy(folder => folder.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var revokedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var affected = await _dbContext.SelectedFolders
                                       .Where(folder => folder.Id == id && folder.RevokedAtUtc == null)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(folder => folder.RevokedAtUtc, revokedAtUtc), cancellationToken)
                                       .ConfigureAwait(false);

        return affected == 1;
    }

    private static SelectedFolderRecord ToRecord(NodeSelectedFolder entity)
    {
        return new SelectedFolderRecord(entity.Id,
            entity.Alias,
            Encoding.UTF8.GetString(entity.HostPath),
            entity.Mode,
            entity.CreatedAtUtc,
            entity.RevokedAtUtc);
    }
}
