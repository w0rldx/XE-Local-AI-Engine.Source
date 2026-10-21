namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class BenchmarkModelEligibility
{
    /// <summary>
    ///     Admits local llama.cpp chat GGUFs only: the chat role AND the chat model kind, since a reranker is stored
    ///     with the chat role and is told apart only by its kind (override, detection, then name).
    /// </summary>
    /// <remarks>
    ///     An attached <c>mmproj</c> projector member is NOT disqualifying: the HF acquisition path auto-attaches one
    ///     to modern text models (gemma-4, Qwen3.x), and it is an optional companion the chat runtime passes as
    ///     <c>--mmproj</c> without changing text generation. The benchmark stays text-only — it never sends image
    ///     content — so a projector-bearing chat model measures the same as a bare one. Genuine vision or
    ///     projector-only models are excluded by their <see cref="GgufRole" />, not by this.
    /// </remarks>
    public static void Validate(InstalledModelSnapshot snapshot, ModelClassificationRecord? classification, string role, string? decisionType = null) =>
        Validate(snapshot.ProviderName, snapshot.Role, snapshot.ModelName, classification, role, decisionType);

    /// <inheritdoc cref="Validate(InstalledModelSnapshot, ModelClassificationRecord, string, string)" />
    public static void Validate(string? providerName, GgufRole ggufRole, string modelName, ModelClassificationRecord? classification, string role, string? decisionType = null)
    {
        if (!string.Equals(providerName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || ggufRole != GgufRole.Chat
            || !LocalGgufModelKindClassifier.IsChatModel(modelName, classification, decisionType))
        {
            throw new BenchmarkEligibilityException($"The selected {role} model is not an eligible local text-generation GGUF.");
        }
    }

    /// <summary>The judge is held to a stricter rule than the primary.</summary>
    /// <remarks>
    ///     A model carrying an auxiliary asset (projector, adapter or draft companion) launches with
    ///     <c>--mmproj</c>/<c>--lora</c>/<c>-md</c>, and the launch receipt records only THAT something extra was loaded,
    ///     never which file, so its judgings could never join a rank cohort: refused at the policy, not left unranked.
    ///     A rubric of verifiable criteria only never spawns the judge, so the policy passes <paramref name="judgeWillRun" />
    ///     false and the rule is not applied to a model that will not run.
    /// </remarks>
    public static void ValidateJudge(InstalledModelSnapshot snapshot, ModelClassificationRecord? classification, string? decisionType = null, bool judgeWillRun = true)
    {
        Validate(snapshot, classification, "judge", decisionType);
        if (judgeWillRun && snapshot.Members.Any(static member => member.Role == InstalledModelPhysicalMemberRole.Projector))
        {
            throw new BenchmarkEligibilityException(
                "The selected judge model carries an auxiliary asset (projector, adapter or draft model). Judgings from such a model cannot be ranked; pick a plain text GGUF.");
        }
    }

    /// <summary>Whether a policy ever spawns its judge model.</summary>
    /// <remarks>
    ///     Always for pairwise judging (the comparison executor has no verifier path), otherwise when any criterion is
    ///     model-decided. A malformed rubric (null list or entry) answers true, so the strict rule holds until
    ///     <see cref="BenchmarkJudgePolicyValidator" /> rejects the rubric itself.
    /// </remarks>
    public static bool JudgeWillRun(BenchmarkJudgeRubricV1? rubric, string? mode) =>
        string.Equals(BenchmarkJudgePolicyModes.Normalize(mode), BenchmarkJudgePolicyModes.Pairwise, StringComparison.Ordinal)
        || rubric?.Criteria is null
        || rubric.Criteria.Any(static criterion => criterion is null || !BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind));
}
