namespace XE_Local_AI_Engine.Client.Services.Eval;

using System.Text.Json;

/// <summary>
///     The plaintext JSON shape persisted to <c>PlaybookAction.EvalResult</c> before a Suggested action may be promoted:
///     ids + pass/fail flags + counts only (no transcripts, no free text), so it is structural — not sensitive. The same
///     shape is read back by the promote gate to decide whether the eval passed and is current
///     (<see cref="ActionVersionAtEval" /> ties the pass to the action's content snapshot — see the gate's staleness
///     check). Positional record so System.Text.Json binds JSON properties to the constructor parameters by name.
///     <see cref="GoldenCaseCount" /> is the number of cases actually evaluated; <see cref="GoldenCaseTotal" /> is the
///     full enabled golden-set size BEFORE the per-run cap, so the operator can see when a run only evaluated a subset.
/// </summary>
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
    IReadOnlyList<PlaybookEvalCaseResult> Cases)
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
