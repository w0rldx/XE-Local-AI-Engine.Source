namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary><see cref="IModelLabExperimentStore" /> over <see cref="NodeChatDbContext" />. The one job at a time is the GPU gate's rule, so there is no version column.</summary>
public sealed class ModelLabExperimentStore : IModelLabExperimentStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ModelLabExperimentStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task CreateRunningAsync(ModelLabExperimentCreate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = Now();
        _ = _dbContext.ModelLabExperiments.Add(new ModelLabExperiment
        {
            Id = command.Id,
            Status = ModelLabExperimentStatus.Running,
            RepoId = command.RepoId,
            Revision = command.Revision,
            BaseArtifactId = command.BaseArtifactId,
            InstalledModelName = command.InstalledModelName,
            Prompt = Encoding.UTF8.GetBytes(command.Prompt),
            LicenseConfirmationJson = command.LicenseConfirmationJson.ToArray(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.ModelLabExperiments.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.ModelLabExperiments.AnyAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.ModelLabExperiments.AsNoTracking()
                                   .OrderByDescending(item => item.CreatedAtUtc)
                                   .ThenByDescending(item => item.Id)
                                   .ToListAsync(cancellationToken);
        return [.. rows.Select(ToRecord)];
    }

    public async Task SetLaunchReceiptAsync(Guid id, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.ModelLabExperiments.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.LaunchReceiptJson = launchReceiptJson?.ToArray();
        row.UpdatedAtUtc = Now();
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(Guid id, ModelLabExperimentCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var row = await _dbContext.ModelLabExperiments.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is not { Status: ModelLabExperimentStatus.Running })
        {
            return;
        }

        row.Status = completion.Status;
        row.ErrorMessage = completion.ErrorMessage is { } message ? Encoding.UTF8.GetBytes(message) : null;
        row.ResultJson = completion.ResultJson?.ToArray();
        row.TraceBlobId = completion.TraceBlobId;
        row.TraceSha256 = completion.TraceSha256;
        row.TraceByteCount = completion.TraceByteCount;
        row.LaunchReceiptJson = null;
        row.UpdatedAtUtc = Now();
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ModelLabExperimentDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.ModelLabExperiments.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
        {
            return ModelLabExperimentDeleteOutcome.NotFound;
        }

        if (row.LaunchReceiptJson is not null)
        {
            return ModelLabExperimentDeleteOutcome.ProcessNotConfirmedStopped;
        }

        _ = _dbContext.ModelLabExperiments.Remove(row);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return ModelLabExperimentDeleteOutcome.Deleted;
    }

    public async Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default)
    {
        var excess = await _dbContext.ModelLabExperiments
                                     .OrderByDescending(item => item.CreatedAtUtc)
                                     .ThenByDescending(item => item.Id)
                                     .Skip(keep)
                                     .Where(item => item.Status != ModelLabExperimentStatus.Running && item.LaunchReceiptJson == null)
                                     .ToListAsync(cancellationToken);
        if (excess.Count == 0)
        {
            return [];
        }

        _dbContext.ModelLabExperiments.RemoveRange(excess);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return [.. excess.Select(static item => item.Id)];
    }

    public async Task<IReadOnlyList<ModelLabLaunchReceipt>> ListLaunchReceiptsAsync(CancellationToken cancellationToken = default)
    {
        // Entities, not a projection: only the materialization interceptor decrypts the column.
        var rows = await _dbContext.ModelLabExperiments.AsNoTracking()
                                   .Where(item => item.LaunchReceiptJson != null)
                                   .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(static row => new ModelLabLaunchReceipt
            {
                ExperimentId = row.Id,
                LaunchReceiptJson = row.LaunchReceiptJson!
            })
        ];
    }

    public async Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _dbContext.ModelLabExperiments
                                          .Where(item => item.Status == ModelLabExperimentStatus.Running)
                                          .ToListAsync(cancellationToken);
        var now = Now();
        foreach (var row in interrupted)
        {
            row.Status = ModelLabExperimentStatus.Failed;
            row.ErrorMessage = Encoding.UTF8.GetBytes("The experiment was interrupted by a host restart.");
            row.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return [.. interrupted.Select(static item => item.Id)];
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static ModelLabExperimentRecord ToRecord(ModelLabExperiment row) =>
        new()
        {
            Id = row.Id,
            Status = row.Status,
            RepoId = row.RepoId,
            Revision = row.Revision,
            BaseArtifactId = row.BaseArtifactId,
            InstalledModelName = row.InstalledModelName,
            Prompt = Encoding.UTF8.GetString(row.Prompt),
            ResultJson = row.ResultJson,
            ErrorMessage = row.ErrorMessage is { } error ? Encoding.UTF8.GetString(error) : null,
            TraceBlobId = row.TraceBlobId,
            TraceSha256 = row.TraceSha256,
            TraceByteCount = row.TraceByteCount,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc
        };
}
