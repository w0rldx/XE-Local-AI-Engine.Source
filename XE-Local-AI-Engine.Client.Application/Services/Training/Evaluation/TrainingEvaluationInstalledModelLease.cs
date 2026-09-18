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

internal sealed class TrainingEvaluationInstalledModelLeaseProvider : ITrainingEvaluationInstalledModelLeaseProvider
{
    private readonly IInstalledModelSnapshotCoordinator _coordinator;
    private readonly IGgufModelStore _models;

    public TrainingEvaluationInstalledModelLeaseProvider(
        IInstalledModelSnapshotCoordinator coordinator,
        IGgufModelStore models)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(models);
        _coordinator = coordinator;
        _models = models;
    }

    public async Task<ITrainingEvaluationInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
    {
        var lease = await _coordinator.AcquireReadSnapshotAsync(modelName, cancellationToken);
        try
        {
            var alias = lease.Snapshot.RegistryAliases.Single(item =>
                string.Equals(item.ModelName, modelName, StringComparison.Ordinal));
            var weight = lease.Snapshot.Members.Single(item =>
                item.Role == InstalledModelPhysicalMemberRole.Weight
                && string.Equals(item.RelativePath, alias.WeightRelativePath, StringComparison.Ordinal));
            var path = await _models.ResolveModelFilePathAsync(modelName, cancellationToken)
                       ?? throw new InvalidOperationException("InstalledModelPathUnavailable");
            return new EvaluationLease(lease,
                path,
                lease.Snapshot.ModelContentFingerprint,
                weight.Sha256,
                weight.SizeBytes);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class EvaluationLease : ITrainingEvaluationInstalledModelLease
    {
        private readonly InstalledModelReadLease _inner;

        public EvaluationLease(
            InstalledModelReadLease inner,
            string modelFilePath,
            string modelContentFingerprint,
            string modelSha256,
            long modelSizeBytes)
        {
            _inner = inner;
            ModelFilePath = modelFilePath;
            ModelContentFingerprint = modelContentFingerprint;
            ModelSha256 = modelSha256;
            ModelSizeBytes = modelSizeBytes;
        }

        public string ModelFilePath { get; }
        public string ModelContentFingerprint { get; }
        public string ModelSha256 { get; }
        public long ModelSizeBytes { get; }

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }
}
