namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The judging modes a policy may name. Pointwise is the default and the only mode currently executed.</summary>
public static class BenchmarkJudgePolicyModes
{
    public const string Pointwise = "pointwise";
    public const string Pairwise = "pairwise";

    public static string Normalize(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? Pointwise : mode;
}
