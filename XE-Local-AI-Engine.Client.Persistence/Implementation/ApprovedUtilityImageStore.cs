namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for the approved utility image registry. The <c>approved_utility_images</c> table is keyed
///     by a string id with a <c>NOCASE</c> collation, so id lookups and the seed key are case-insensitive without any
///     LINQ-side comparer. No column is encrypted. The seed upsert preserves the operator-set <c>Enabled</c> toggle and
///     the original creation/usage timestamps; <c>image_reference</c> is mutated only here, never from any API.
/// </summary>
public sealed class ApprovedUtilityImageStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IApprovedUtilityImageStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<ApprovedUtilityImageRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ApprovedUtilityImages
                                       .AsNoTracking()
                                       .OrderBy(image => image.ApprovedImageId)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ApprovedUtilityImageRecord?> GetByIdAsync(string approvedImageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedImageId);

        var entity = await _dbContext.ApprovedUtilityImages
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(image => image.ApprovedImageId == approvedImageId, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ApprovedUtilityImageRecord> UpsertSeedAsync(ApprovedUtilityImageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApprovedImageId);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // Load tracked so an existing operator Enabled toggle (and the original creation/usage timestamps) survive the
        // code re-seed.
        var entity = await _dbContext.ApprovedUtilityImages
                                     .FirstOrDefaultAsync(image => image.ApprovedImageId == record.ApprovedImageId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ApprovedUtilityImage
            {
                ApprovedImageId = record.ApprovedImageId,
                DisplayName = record.DisplayName,
                Description = record.Description,
                Purpose = record.Purpose,
                ImageReference = record.ImageReference,
                SourceUrl = record.SourceUrl,
                UpstreamVersion = record.UpstreamVersion,
                Enabled = record.Enabled,
                DeprecatedAtUtc = record.DeprecatedAtUtc,
                ReplacementApprovedImageId = record.ReplacementApprovedImageId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastUsedAtUtc = null,
                LastSuccessfulRunAtUtc = null,
                DiagnosticsJson = record.DiagnosticsJson
            };

            _ = _dbContext.ApprovedUtilityImages.Add(entity);
        }
        else
        {
            // Refresh the code-owned fields only; preserve the operator Enabled toggle and the usage/creation timestamps.
            entity.DisplayName = record.DisplayName;
            entity.Description = record.Description;
            entity.Purpose = record.Purpose;
            entity.ImageReference = record.ImageReference;
            entity.SourceUrl = record.SourceUrl;
            entity.UpstreamVersion = record.UpstreamVersion;
            entity.DeprecatedAtUtc = record.DeprecatedAtUtc;
            entity.ReplacementApprovedImageId = record.ReplacementApprovedImageId;
            entity.DiagnosticsJson = record.DiagnosticsJson;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ApprovedUtilityImageRecord?> SetEnabledAsync(string approvedImageId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedImageId);

        var entity = await _dbContext.ApprovedUtilityImages
                                     .FirstOrDefaultAsync(image => image.ApprovedImageId == approvedImageId, cancellationToken)
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

    public async Task<ApprovedUtilityImageRecord?> TouchUsedAsync(
        string approvedImageId,
        long lastUsedAtUtc,
        long? lastSuccessfulRunAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedImageId);

        var entity = await _dbContext.ApprovedUtilityImages
                                     .FirstOrDefaultAsync(image => image.ApprovedImageId == approvedImageId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.LastUsedAtUtc = lastUsedAtUtc;

        if (lastSuccessfulRunAtUtc is not null)
        {
            entity.LastSuccessfulRunAtUtc = lastSuccessfulRunAtUtc;
        }

        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    private static ApprovedUtilityImageRecord ToRecord(ApprovedUtilityImage entity)
    {
        return new ApprovedUtilityImageRecord(
            entity.ApprovedImageId,
            entity.DisplayName,
            entity.Description,
            entity.Purpose,
            entity.ImageReference,
            entity.SourceUrl,
            entity.UpstreamVersion,
            entity.Enabled,
            entity.DeprecatedAtUtc,
            entity.ReplacementApprovedImageId,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastUsedAtUtc,
            entity.LastSuccessfulRunAtUtc,
            entity.DiagnosticsJson);
    }
}
