namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public interface ITrainingEvaluationInstalledModelLease : IAsyncDisposable
{
    string ModelFilePath { get; }
    string ModelContentFingerprint { get; }
    string ModelSha256 { get; }
    long ModelSizeBytes { get; }
}

public interface ITrainingEvaluationInstalledModelLeaseProvider
{
    Task<ITrainingEvaluationInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken);
}

internal sealed class TrainingEvaluationInstalledModelLeaseProvider(
    IInstalledModelSnapshotCoordinator coordinator,
    IGgufModelStore models) : ITrainingEvaluationInstalledModelLeaseProvider
{
    private readonly IInstalledModelSnapshotCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IGgufModelStore _models = models ?? throw new ArgumentNullException(nameof(models));

    public async Task<ITrainingEvaluationInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
    {
        var lease = await _coordinator.AcquireReadSnapshotAsync(modelName, cancellationToken).ConfigureAwait(false);
        try
        {
            var alias = lease.Snapshot.RegistryAliases.Single(item =>
                string.Equals(item.ModelName, modelName, StringComparison.Ordinal));
            var weight = lease.Snapshot.Members.Single(item =>
                item.Role == InstalledModelPhysicalMemberRole.Weight
                && string.Equals(item.RelativePath, alias.WeightRelativePath, StringComparison.Ordinal));
            var path = await _models.ResolveModelFilePathAsync(modelName, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("InstalledModelPathUnavailable");
            return new EvaluationLease(lease,
                path,
                lease.Snapshot.ModelContentFingerprint,
                weight.Sha256,
                weight.SizeBytes);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class EvaluationLease(
        InstalledModelReadLease inner,
        string modelFilePath,
        string modelContentFingerprint,
        string modelSha256,
        long modelSizeBytes) : ITrainingEvaluationInstalledModelLease
    {
        public string ModelFilePath { get; } = modelFilePath;
        public string ModelContentFingerprint { get; } = modelContentFingerprint;
        public string ModelSha256 { get; } = modelSha256;
        public long ModelSizeBytes { get; } = modelSizeBytes;

        public ValueTask DisposeAsync() =>
            inner.DisposeAsync();
    }
}
