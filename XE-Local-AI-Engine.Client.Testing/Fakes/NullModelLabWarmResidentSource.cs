namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>No warm Model Lab worker: no resident row, and an eject that has nothing to do.</summary>
/// <remarks>For tests that build their consumer without the experiment service, which is the node's own source.</remarks>
public sealed class NullModelLabWarmResidentSource : IModelLabWarmResidentSource
{
    /// <inheritdoc />
    public ModelLabWarmResident? GetResident() =>
        null;

    /// <inheritdoc />
    public Task<ModelLabEjectOutcome> EjectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ModelLabEjectOutcome.Gone);
}
