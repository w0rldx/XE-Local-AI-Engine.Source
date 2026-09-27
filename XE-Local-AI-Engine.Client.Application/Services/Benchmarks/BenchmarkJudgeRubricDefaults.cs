namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
/// The rubrics the judge-policy form offers. Every variant keeps the same criterion ids and weights so switching preset
/// only rewrites the wording an operator can then edit.
/// </summary>
public static class BenchmarkJudgeRubricDefaults
{
    public const string CorrectnessId = "correctness";
    public const string ReasoningId = "reasoning";
    public const string CompletenessId = "completeness";
    public const string InstructionAdherenceId = "instruction_adherence";
    public const string ClarityId = "clarity";

    public static BenchmarkJudgeRubricV1 Default() =>
        Build("Correctness and task completion",
            "Does the output do what the task asked, correctly? 0 = wrong or unrelated; 5 = partially correct with material gaps; 10 = fully correct and the task is complete.",
            "Reasoning and accuracy",
            "Is the reasoning sound and every claim accurate? 0 = incoherent or fabricated; 5 = broadly reasonable with some unsupported steps; 10 = sound throughout with no invented facts.",
            "Completeness",
            "Are all parts of the task covered at useful depth? 0 = most of the task is unaddressed; 5 = the main part is covered, secondary parts are missing; 10 = every part is covered at appropriate depth.",
            "Instruction and format adherence",
            "Are the stated instructions, constraints and output format followed? 0 = ignored; 5 = followed loosely with deviations; 10 = followed exactly.",
            "Clarity",
            "Is the output clear, well organised and free of filler? 0 = confusing or unreadable; 5 = understandable but rambling or poorly structured; 10 = concise, well structured and easy to follow.");

    public static BenchmarkJudgeRubricV1 Programming() =>
        Build("Correctness and task completion",
            "Does the code do what was asked and would it actually run? 0 = does not compile or solves a different problem; 5 = solves the happy path but breaks on edge cases; 10 = correct including edge cases and error handling.",
            "Reasoning and accuracy",
            "Are the technical choices and any explanation accurate? 0 = wrong APIs or invented libraries; 5 = workable but with questionable choices; 10 = idiomatic, accurate and well justified.",
            "Completeness",
            "Are all required behaviours, files and supporting pieces delivered? 0 = fragments only; 5 = core implementation without tests or wiring; 10 = complete and ready to drop in.",
            "Instruction and format adherence",
            "Are the language, API, style and output-format constraints respected? 0 = ignored; 5 = mostly respected with deviations; 10 = respected exactly.",
            "Clarity",
            "Is the code readable, with sensible names and no dead weight? 0 = unreadable; 5 = works but is hard to follow; 10 = clear, minimal and self-explanatory.");

    public static BenchmarkJudgeRubricV1 Reasoning() =>
        Build("Correctness and task completion",
            "Is the final answer correct and does it answer the question asked? 0 = wrong; 5 = partially right or answers a nearby question; 10 = fully correct answer to the actual question.",
            "Reasoning and accuracy",
            "Do the steps actually support the conclusion? 0 = no derivation or invalid logic; 5 = mostly valid with a gap or an unstated assumption; 10 = every step valid and stated.",
            "Completeness",
            "Are the relevant cases, constraints and alternatives considered? 0 = single unexamined guess; 5 = the main line only; 10 = alternatives weighed and the choice justified.",
            "Instruction and format adherence",
            "Are the requested reasoning depth, answer form and constraints honoured? 0 = ignored; 5 = partly honoured; 10 = honoured exactly.",
            "Clarity",
            "Can a reader follow the argument end to end? 0 = incoherent; 5 = followable but disorganised; 10 = a clean chain from premises to conclusion.");

    public const string FinalAnswerId = "final_answer";
    public const string OutputLengthId = "output_length";
    public const string NoRefusalId = "no_refusal";

    /// <summary>
    ///     The one preset that costs no GPU: every criterion is decided server-side, so a project judging under it
    ///     completes with no llama-server spawn at all.
    /// </summary>
    /// <remarks>
    ///     Unlike the three model-judged presets, this one does NOT share their criterion ids and weights — it cannot,
    ///     because a verifiable criterion is a different question. <see cref="FinalAnswerId" />'s expected value is a
    ///     placeholder the operator must edit to their task; the other two are usable as they stand.
    /// </remarks>
    public static BenchmarkJudgeRubricV1 Verifiable() =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1(FinalAnswerId,
                "Final answer",
                "The final numeric answer, read from \\boxed{}, from a #### marker, from an \"answer is\" phrase, or as the last number in the output. Edit the expected value to your task.",
                60,
                BenchmarkJudgeCriterionKinds.MathAnswer,
                """{"expected":0}"""),
            new BenchmarkJudgeRubricCriterionV1(OutputLengthId,
                "Output length",
                "The answer stays inside a sane length budget instead of padding.",
                20,
                BenchmarkJudgeCriterionKinds.Constraint,
                """{"maxWords":800}"""),
            new BenchmarkJudgeRubricCriterionV1(NoRefusalId,
                "No refusal",
                "The model attempted the task rather than declining it.",
                20,
                BenchmarkJudgeCriterionKinds.Regex,
                """{"pattern":"(?i:i (?:cannot|can not|am unable to|won't|will not) )","mustMatch":false}""")
        ]);

    public const string SolutionRunsId = "solution_runs";

    /// <summary>
    ///     The execution preset: one criterion, decided by RUNNING the answer's code against the operator's hidden
    ///     tests rather than by asking a model to read it.
    /// </summary>
    /// <remarks>
    ///     A single criterion at full weight on purpose — the tests either pass or they do not, and averaging that
    ///     against a model's opinion of the same code reintroduces exactly the judgement this preset replaces. Both
    ///     halves are placeholders the operator must edit: <c>testCode</c> is the hidden test suite, <c>exports</c>
    ///     names the symbols those tests call. It needs <c>Compute:Enabled</c> on a node whose sandbox can isolate and
    ///     enforce resource ceilings; where it cannot, runs are left unranked with <c>verifier-unavailable</c>, not 0.
    /// </remarks>
    public static BenchmarkJudgeRubricV1 CodeExecution() =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1(SolutionRunsId,
                "Solution passes the hidden tests",
                "The Python code in the answer is run in the compute sandbox against the operator's tests. Edit the test code and the exported names to your task.",
                100,
                BenchmarkJudgeCriterionKinds.PythonTests,
                """{"testCode":"assert solve(2) == 4\nassert solve(0) == 0","exports":["solve"],"timeoutSeconds":30}""")
        ]);

    private static BenchmarkJudgeRubricV1 Build(string correctnessTitle,
        string correctnessDescription,
        string reasoningTitle,
        string reasoningDescription,
        string completenessTitle,
        string completenessDescription,
        string adherenceTitle,
        string adherenceDescription,
        string clarityTitle,
        string clarityDescription) =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1(CorrectnessId, correctnessTitle, correctnessDescription, 40),
            new BenchmarkJudgeRubricCriterionV1(ReasoningId, reasoningTitle, reasoningDescription, 25),
            new BenchmarkJudgeRubricCriterionV1(CompletenessId, completenessTitle, completenessDescription, 15),
            new BenchmarkJudgeRubricCriterionV1(InstructionAdherenceId, adherenceTitle, adherenceDescription, 10),
            new BenchmarkJudgeRubricCriterionV1(ClarityId, clarityTitle, clarityDescription, 10)
        ]);
}
