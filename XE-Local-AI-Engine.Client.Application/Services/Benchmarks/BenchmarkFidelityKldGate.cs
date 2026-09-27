namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The KLD display gate as a state: every reader that serves a run's fidelity block — the API and both exports —
///     derives <c>kldState</c> here, so a stale figure cannot be withheld by one and badged differently by another.
/// </summary>
public static class BenchmarkFidelityKldGate
{
    /// <summary>
    ///     <see cref="BenchmarkFidelityKldStates.None" /> without a KLD measurement, otherwise
    ///     <see cref="BenchmarkFidelityKldStates.Ok" /> or <see cref="BenchmarkFidelityKldStates.Stale" /> by
    ///     <see cref="BenchmarkKldCacheKey.IsComparable" />. A stale figure's numbers are withheld by the caller.
    /// </summary>
    public static string State(BenchmarkRunFidelity fidelity, string? expectedKldBaseLogitsDigest)
    {
        ArgumentNullException.ThrowIfNull(fidelity);
        if (fidelity.KldMean is null)
        {
            return BenchmarkFidelityKldStates.None;
        }

        return BenchmarkKldCacheKey.IsComparable(fidelity.KldBaseLogitsDigest, expectedKldBaseLogitsDigest)
            ? BenchmarkFidelityKldStates.Ok
            : BenchmarkFidelityKldStates.Stale;
    }
}
