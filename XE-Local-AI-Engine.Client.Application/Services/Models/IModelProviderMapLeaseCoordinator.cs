namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.Models.Implementation;

public interface IModelProviderMapLeaseCoordinator
{
    ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(string modelName, CancellationToken cancellationToken = default);
    ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(IEnumerable<string> modelNames, CancellationToken cancellationToken = default);

    ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(string modelName,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default);

    ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(IEnumerable<string> modelNames,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default);
}
