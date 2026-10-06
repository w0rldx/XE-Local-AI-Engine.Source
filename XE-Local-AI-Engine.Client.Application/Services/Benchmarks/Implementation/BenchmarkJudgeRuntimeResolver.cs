namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <inheritdoc />
public sealed class BenchmarkJudgeRuntimeResolver : IBenchmarkJudgeRuntimeResolver
{
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels;
    private readonly IBenchmarkPhaseLaunchResolver _launchResolver;
    private readonly IModelClassificationStore _classifications;

    public BenchmarkJudgeRuntimeResolver(IBenchmarkInstalledModelLeaseProvider installedModels,
        IBenchmarkPhaseLaunchResolver launchResolver,
        IModelClassificationStore classifications)
    {
        ArgumentNullException.ThrowIfNull(installedModels);
        ArgumentNullException.ThrowIfNull(launchResolver);
        ArgumentNullException.ThrowIfNull(classifications);
        _installedModels = installedModels;
        _launchResolver = launchResolver;
        _classifications = classifications;
    }

    public async Task<BenchmarkJudgeRuntimeResolution> ResolveAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var lease = await _installedModels.AcquireAsync(policy.Model.ModelName, cancellationToken);
        BenchmarkModelEligibility.ValidateJudge(lease.Snapshot, await _classifications.GetByNameAsync(lease.Snapshot.ModelName, cancellationToken));
        if (!string.Equals(lease.Snapshot.ModelContentFingerprint, policy.Model.ModelContentFingerprint, StringComparison.Ordinal))
        {
            throw new BenchmarkEligibilityException("The installed judge model changed after the judge policy was created.");
        }

        var model = BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot);
        var capabilities = await _launchResolver.InspectAsync(cancellationToken);
        var variant = await _launchResolver.SelectVariantAsync(capabilities, cancellationToken);

        // The judge is scoring, not being measured: it never takes a run's KV pick, only Auto.
        var launch = await _launchResolver.ResolveAsync(model.ModelName,
            policy.RequestedContextTokens,
            requestedKvCacheType: null,
            capabilities,
            variant,
            cancellationToken);
        return new BenchmarkJudgeRuntimeResolution
        {
            Runtime = new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                model,
                policy.RequestedContextTokens,
                launch.Runtime,
                BenchmarkFrozenPolicies.DeterministicSampling()),
            Intent = launch.Intent
        };
    }
}
