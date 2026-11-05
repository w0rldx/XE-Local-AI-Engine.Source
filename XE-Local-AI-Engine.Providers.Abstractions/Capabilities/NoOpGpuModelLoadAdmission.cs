namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>No-op <see cref="IGpuModelLoadAdmission" /> floor: admits immediately with no serialization.</summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> so a provider-only host (or a test) resolves a gate even when the application
///     layer has not registered the real, metric-emitting serializer. That real gate, registered by the composition
///     root via a plain <c>AddSingleton</c>, wins over this floor.
/// </remarks>
public sealed class NoOpGpuModelLoadAdmission : IGpuModelLoadAdmission
{
    /// <inheritdoc />
    public Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IDisposable>(new NoOpTicket());
    }

    private sealed class NoOpTicket : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
