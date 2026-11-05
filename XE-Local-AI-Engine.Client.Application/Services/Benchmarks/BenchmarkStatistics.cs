namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Order statistics shared by the benchmark scorers, so their intervals and latency percentiles agree.</summary>
internal static class BenchmarkStatistics
{
    /// <summary>Nearest-rank percentile of an already ascending, non-empty list.</summary>
    public static T NearestRank<T>(IReadOnlyList<T> ordered, double quantile)
    {
        var rank = (int)Math.Ceiling(quantile * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }
}
