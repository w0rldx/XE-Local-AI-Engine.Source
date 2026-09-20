namespace XE_Local_AI_Engine.Client.Services.Eval;

using System.Text.Json;

/// <summary>
///     The plaintext JSON persisted to <c>PlaybookAction.EvalResult</c> before a Suggested action may be promoted:
///     ids, pass/fail flags and counts only, so it is structural rather than sensitive.
/// </summary>
/// <remarks>
///     The promote gate reads it back to decide whether the eval passed and is current, with
///     <see cref="ActionVersionAtEval" /> tying the pass to the action's content snapshot. It is a positional record,
///     so System.Text.Json binds by constructor parameter name. <see cref="GoldenCaseCount" /> is what was actually
///     evaluated and <see cref="GoldenCaseTotal" /> the full enabled set BEFORE the cap, so an INCOMPLETE run is
///     visible and refused, and the gate recomputes <see cref="EvaluationFingerprint" /> and requires a match.
/// </remarks>
public sealed record PlaybookEvalResult(
    bool Passed,
    long EvaluatedAtUtc,
    int ActionVersionAtEval,
    string ModelName,
    int GoldenCaseCount,
    int GoldenCaseTotal,
    int BaselinePassCount,
    int CandidatePassCount,
    int RegressedCaseCount,
    int ImprovedCaseCount,
    IReadOnlyList<PlaybookEvalCaseResult> Cases,
    string EvaluationFingerprint = "")
{
    /// <summary>
    ///     Cached (de)serialization options for the persisted eval result (CA1869). Web defaults so the JSON keys are
    ///     camelCase, matching the panel + the gate that read this column back.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
///     Per-case outcome inside a <see cref="PlaybookEvalResult" />. <see cref="ScoredBy" /> records which scoring path
///     decided the case (<c>"assertion"</c> = deterministic phrase check, <c>"judge"</c> = node-local LLM rubric);
///     <see cref="Regressed" /> is <c>BaselinePass &amp;&amp; !CandidatePass</c> — the gate criterion.
/// </summary>
public sealed record PlaybookEvalCaseResult(
    Guid GoldenCaseId,
    string ScoredBy,
    bool BaselinePass,
    bool CandidatePass,
    bool Regressed);
