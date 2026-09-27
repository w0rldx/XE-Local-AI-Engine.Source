namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The rubric-criterion vocabulary. <see cref="Llm" /> is the compatibility default for criteria without an
///     explicit kind and is the only kind that costs a model turn; every other kind is checked server-side against
///     the graded answer with no inference at all.
/// </summary>
public static class BenchmarkJudgeCriterionKinds
{
    public const string Llm = "llm";
    public const string Exact = "exact";
    public const string Regex = "regex";
    public const string JsonSchema = "jsonSchema";
    public const string MathAnswer = "mathAnswer";
    public const string Constraint = "constraint";

    /// <summary>
    ///     Execution scoring: the answer's code is run against the operator's tests in the compute sandbox, and the
    ///     criterion passes iff every collected case passed.
    /// </summary>
    /// <remarks>
    ///     The only kind that is not a pure function of the answer text, and the only one that can be UNSCORABLE
    ///     rather than merely failed.
    /// </remarks>
    public const string PythonTests = "pythonTests";

    /// <summary>Whether this kind is decided server-side rather than by a model.</summary>
    public static bool IsVerifiable(string? kind) =>
        kind is Exact or Regex or JsonSchema or MathAnswer or Constraint or PythonTests;

    /// <summary>
    ///     Whether deciding this kind means EXECUTING something. Pure kinds go through
    ///     <see cref="BenchmarkJudgeVerifiers" />, which is side-effect free and synchronous; this one goes through the
    ///     compute sandbox and is therefore async, refusable, and kept on its own path.
    /// </summary>
    public static bool IsExecutionVerified(string? kind) =>
        string.Equals(Normalize(kind), PythonTests, StringComparison.Ordinal);

    /// <summary>The kind a criterion carries, treating an absent value as the legacy-compatible default.</summary>
    public static string Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? Llm : kind;
}
