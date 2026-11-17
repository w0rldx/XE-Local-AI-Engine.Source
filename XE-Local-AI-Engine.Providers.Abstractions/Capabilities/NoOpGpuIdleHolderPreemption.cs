namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>No-op <see cref="IGpuIdleHolderPreemption" /> floor for provider-only hosts and tests: there is never an idle holder.</summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> by each provider that raises demand; the application's broker, registered with a
///     plain <c>AddSingleton</c>, wins over it.
/// </remarks>
public sealed class NoOpGpuIdleHolderPreemption : IGpuIdleHolderPreemption
{
    /// <summary>The shared instance, for a supervisor or gate constructed without a broker.</summary>
    public static readonly NoOpGpuIdleHolderPreemption Instance = new();

    /// <inheritdoc />
    public bool IsIdlePreemptible => false;

    /// <inheritdoc />
    public void RequestPreemption()
    {
    }

    /// <inheritdoc />
    public Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result: true);
    }
}
