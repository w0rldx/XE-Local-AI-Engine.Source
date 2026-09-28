namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class PreparedGgufAcquisition : IAsyncDisposable
{
    private InstalledModelMutationLease? _lease;

    internal PreparedGgufAcquisition(ResolvedGgufAcquisitionIdentity identity,
        GgufAcquisitionDisposition disposition,
        ProviderMapDisposition providerMapDisposition,
        InstalledModelMutationLease? lease,
        Guid? activeOperationId)
    {
        Identity = identity;
        Disposition = disposition;
        ProviderMapDisposition = providerMapDisposition;
        _lease = lease;
        ActiveOperationId = activeOperationId;
    }

    public ResolvedGgufAcquisitionIdentity Identity { get; }
    public GgufAcquisitionDisposition Disposition { get; }
    public ProviderMapDisposition ProviderMapDisposition { get; }
    public Guid? ActiveOperationId { get; }
    public InstalledModelMutationLease Lease => _lease ?? throw new ObjectDisposedException(nameof(PreparedGgufAcquisition));

    public InstalledModelMutationLease TransferLease()
    {
        return Interlocked.Exchange(ref _lease, null) ?? throw new InvalidOperationException("The acquisition reservation lease was already transferred.");
    }

    public async ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }
}
