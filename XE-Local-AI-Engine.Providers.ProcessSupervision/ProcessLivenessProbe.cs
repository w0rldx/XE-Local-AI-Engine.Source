namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>The reuse-path liveness probe every runtime supervisor runs, bounded by its own time budget.</summary>
public static class ProcessLivenessProbe
{
    /// <summary>
    ///     Runs <paramref name="checkResponsive" /> on a token that also cancels after <paramref name="budget" />, so a
    ///     hung child cannot stall the reuse hot path for the whole HTTP-client timeout.
    /// </summary>
    /// <remarks>
    ///     A probe that runs out of its own budget counts as unresponsive; a cancelled <paramref name="ct" /> propagates
    ///     as <see cref="OperationCanceledException" />.
    /// </remarks>
    public static async Task<bool> CheckResponsiveWithinAsync(Func<CancellationToken, Task<bool>> checkResponsive, TimeSpan budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkResponsive);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(budget);
        try
        {
            return await checkResponsive(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
