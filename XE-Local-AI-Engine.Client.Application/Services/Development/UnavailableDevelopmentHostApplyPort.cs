namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class UnavailableDevelopmentHostApplyPort : IDevelopmentHostApplyPort
{
    public Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(DevelopmentHostApplyState.Ambiguous);
    }

    public Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        throw new InvalidOperationException("No trusted Development host apply adapter is registered. Gate 3 supplies that adapter.");
    }
}

