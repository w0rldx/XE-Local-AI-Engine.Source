namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

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
                                     .FirstOrDefaultAsync(folder => folder.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderAlias);

        var entity = await _dbContext.SelectedFolders
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(folder => folder.Alias == folderAlias, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.SelectedFolders
                                       .AsNoTracking()
                                       .OrderBy(folder => folder.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static SelectedFolderRecord ToRecord(NodeSelectedFolder entity)
    {
        return new SelectedFolderRecord(entity.Id, entity.Alias, Encoding.UTF8.GetString(entity.HostPath), entity.Mode, entity.CreatedAtUtc);
    }
}
