namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for llama-server inference profiles. Holds one live config per
///     <c>(machine_key, model_name, role, backend)</c> key: an explore upsert overwrites the single config (latest explore
///     wins), and freeze/stale transitions move its status. The freeze transition runs inside a single transaction and is
///     gated on the row being <see cref="InferenceProfileStatus.Explored" />, mirroring the transactional promotion of
///     <see cref="ModelFitSnapshotStore" />. All columns are plaintext (no secrets), so this store touches no encryption
///     interceptor.
/// </summary>
public sealed class InferenceProfileStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IInferenceProfileStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<InferenceProfileRecord> CreateOrUpdateExploredAsync(InferenceProfileInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var existing = await _dbContext.InferenceProfiles
                                       .FirstOrDefaultAsync(profile =>
                                               profile.MachineKey == input.MachineKey &&
                                               profile.ModelName == input.ModelName &&
                                               profile.Role == input.Role &&
                                               profile.Backend == input.Backend,
                                           cancellationToken)
                                       .ConfigureAwait(false);

        if (existing is null)
        {
            var entity = new InferenceProfile
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = now,
                Status = InferenceProfileStatus.Explored,
                BenchmarkSnapshotId = null,
                GlobalFreeVramAtFreezeBytes = null,
                ProcessBudgetVramAtFreezeBytes = null
            };

            ApplyExploredFields(entity, input, now);

            _ = _dbContext.InferenceProfiles.Add(entity);
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ToRecord(entity);
        }

        // Re-explore: overwrite the single config and reset to Explored, clearing any prior freeze justification so a
        // stale/frozen profile drops back to a fresh draft that the next benchmark must re-justify.
        existing.Status = InferenceProfileStatus.Explored;
        existing.BenchmarkSnapshotId = null;
        existing.GlobalFreeVramAtFreezeBytes = null;
        existing.ProcessBudgetVramAtFreezeBytes = null;
        ApplyExploredFields(existing, input, now);

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(existing);
    }

    public async Task<InferenceProfileRecord?> MarkFrozenAsync(Guid id,
        Guid benchmarkSnapshotId,
        long? globalFreeVramAtFreezeBytes,
        long? processBudgetVramAtFreezeBytes,
        CancellationToken cancellationToken = default)
    {
        // Freezing is the meaningful promotion (analogue of a Succeeded snapshot), so it runs inside a single
        // transaction. The freeze gate only promotes a row still in Explored — a successful benchmark is the sole
        // justification; a re-explored or already-frozen row is left untouched.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var entity = await _dbContext.InferenceProfiles
                                     .FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null || entity.Status != InferenceProfileStatus.Explored)
        {
            return null;
        }

        entity.Status = InferenceProfileStatus.Frozen;
        entity.BenchmarkSnapshotId = benchmarkSnapshotId;
        entity.GlobalFreeVramAtFreezeBytes = globalFreeVramAtFreezeBytes;
        entity.ProcessBudgetVramAtFreezeBytes = processBudgetVramAtFreezeBytes;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<InferenceProfileRecord?> MarkStaleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.InferenceProfiles
                                     .FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.Status = InferenceProfileStatus.Stale;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<InferenceProfileRecord?> GetByKeyAsync(string machineKey,
        string modelName,
        int role,
        string backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);

        var entity = await _dbContext.InferenceProfiles
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(profile =>
                                             profile.MachineKey == machineKey &&
                                             profile.ModelName == modelName &&
                                             profile.Role == role &&
                                             profile.Backend == backend,
                                         cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<InferenceProfileRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.InferenceProfiles
                                       .AsNoTracking()
                                       .OrderBy(profile => profile.ModelName)
                                       .ThenBy(profile => profile.Role)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static void ApplyExploredFields(InferenceProfile entity, InferenceProfileInput input, long now)
    {
        entity.MachineKey = input.MachineKey;
        entity.ModelName = input.ModelName;
        entity.Role = input.Role;
        entity.Backend = input.Backend;
        entity.LlamacppBuild = input.LlamacppBuild;
        entity.Quant = input.Quant;
        entity.CtxSize = input.CtxSize;
        entity.NGpuLayers = input.NGpuLayers;
        entity.TensorSplit = input.TensorSplit;
        entity.OverrideTensor = input.OverrideTensor;
        entity.KvTypeK = input.KvTypeK;
        entity.KvTypeV = input.KvTypeV;
        entity.FlashAttn = input.FlashAttn;
        entity.NParams = input.NParams;
        entity.IsMoe = input.IsMoe;
        entity.ExpertCount = input.ExpertCount;
        entity.LaunchPolicyFingerprintVersion = input.LaunchPolicyFingerprintVersion;
        entity.LaunchPolicyFingerprint = input.LaunchPolicyFingerprint;
        entity.UpdatedAtUtc = now;
    }

    private static InferenceProfileRecord ToRecord(InferenceProfile entity)
    {
        return new InferenceProfileRecord(entity.Id,
            entity.MachineKey,
            entity.ModelName,
            entity.Role,
            entity.Backend,
            entity.LlamacppBuild,
            entity.Quant,
            entity.CtxSize,
            entity.NGpuLayers,
            entity.TensorSplit,
            entity.OverrideTensor,
            entity.KvTypeK,
            entity.KvTypeV,
            entity.FlashAttn,
            entity.NParams,
            entity.IsMoe,
            entity.ExpertCount,
            entity.Status,
            entity.BenchmarkSnapshotId,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LaunchPolicyFingerprintVersion,
            entity.LaunchPolicyFingerprint,
            entity.GlobalFreeVramAtFreezeBytes,
            entity.ProcessBudgetVramAtFreezeBytes);
    }
}
