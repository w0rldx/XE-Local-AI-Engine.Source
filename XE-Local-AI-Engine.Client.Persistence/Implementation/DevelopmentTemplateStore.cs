namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentTemplateStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IDevelopmentTemplateStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<DevelopmentTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _dbContext.DevelopmentTemplates.AsNoTracking()
                                        .OrderBy(entity => entity.Alias)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return templates.Select(Snapshot).ToArray();
    }

    public async Task<DevelopmentTemplateSnapshot> GetAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await _dbContext.DevelopmentTemplates.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == templateId, cancellationToken)
                                       .ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Development template '{templateId}' was not found.");
        return Snapshot(template);
    }

    public async Task<DevelopmentTemplateSnapshot> AddAsync(string templateAlias,
        string hostPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var template = new DevelopmentTemplate
        {
            Id = Guid.NewGuid(),
            Alias = templateAlias,
            HostPath = Encoding.UTF8.GetBytes(hostPath),
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Version = 1
        };
        _dbContext.DevelopmentTemplates.Add(template);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // The unique alias index is the authority; a pre-check would still race two concurrent adds.
            throw new DevelopmentTemplateAliasInUseException($"A Development template named '{templateAlias}' already exists.", exception);
        }

        return Snapshot(template);
    }

    public async Task<bool> RemoveAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await _dbContext.DevelopmentTemplates
                                       .SingleOrDefaultAsync(entity => entity.Id == templateId, cancellationToken)
                                       .ConfigureAwait(false);
        if (template is null)
        {
            return false;
        }

        // Materialization rows deliberately survive: they carry their own copy of the template path and commit, so a
        // project created from this template keeps its provenance after the template is unregistered.
        _dbContext.DevelopmentTemplates.Remove(template);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task RecordMaterializationAsync(DevelopmentTemplateMaterializationSnapshot materialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        _dbContext.DevelopmentTemplateMaterializations.Add(new DevelopmentTemplateMaterialization
        {
            SelectedFolderId = materialization.SelectedFolderId,
            TemplateId = materialization.TemplateId,
            TemplateAlias = materialization.TemplateAlias,
            TemplatePath = Encoding.UTF8.GetBytes(materialization.TemplatePath),
            TemplateCommit = materialization.TemplateCommit,
            CreatedAtUtc = materialization.CreatedAtUtc
        });
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevelopmentTemplateMaterializationSnapshot?> FindMaterializationAsync(Guid selectedFolderId,
        CancellationToken cancellationToken = default)
    {
        var materialization = await _dbContext.DevelopmentTemplateMaterializations.AsNoTracking()
                                              .SingleOrDefaultAsync(entity => entity.SelectedFolderId == selectedFolderId, cancellationToken)
                                              .ConfigureAwait(false);
        return materialization is null
            ? null
            : new DevelopmentTemplateMaterializationSnapshot(materialization.SelectedFolderId,
                materialization.TemplateId,
                materialization.TemplateAlias,
                Encoding.UTF8.GetString(materialization.TemplatePath),
                materialization.TemplateCommit,
                materialization.CreatedAtUtc);
    }

    private static DevelopmentTemplateSnapshot Snapshot(DevelopmentTemplate template) =>
        new(template.Id,
            template.Alias,
            Encoding.UTF8.GetString(template.HostPath),
            template.CreatedAtUtc,
            template.Version);
}
