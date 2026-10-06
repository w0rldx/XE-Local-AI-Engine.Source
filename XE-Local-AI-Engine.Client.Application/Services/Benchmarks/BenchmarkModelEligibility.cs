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
    public static void Validate(InstalledModelSnapshot snapshot, ModelClassificationRecord? classification, string role) =>
        Validate(snapshot.ProviderName, snapshot.Role, snapshot.ModelName, classification, role);

    /// <inheritdoc cref="Validate(InstalledModelSnapshot, ModelClassificationRecord, string)" />
    public static void Validate(string? providerName, GgufRole ggufRole, string modelName, ModelClassificationRecord? classification, string role)
    {
        if (!string.Equals(providerName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || ggufRole != GgufRole.Chat
            || !LocalGgufModelKindClassifier.IsChatModel(modelName, classification))
        {
            throw new BenchmarkEligibilityException($"The selected {role} model is not an eligible local text-generation GGUF.");
        }
    }

    /// <summary>The judge is held to a stricter rule than the primary.</summary>
    /// <remarks>
    ///     A model carrying an auxiliary asset (a projector, and by extension any adapter or draft companion) launches
    ///     with <c>--mmproj</c>/<c>--lora</c>/<c>-md</c>, and the launch receipt records only THAT something extra was
    ///     loaded, never which file. Such a judging can never be shown to be the same execution as another, so it
    ///     could never join a rank cohort — refuse it at the policy instead of letting every run it scores come out
    ///     permanently unranked.
    /// </remarks>
    public static void ValidateJudge(InstalledModelSnapshot snapshot, ModelClassificationRecord? classification)
    {
        Validate(snapshot, classification, "judge");
        if (snapshot.Members.Any(static member => member.Role == InstalledModelPhysicalMemberRole.Projector))
        {
            throw new BenchmarkEligibilityException(
                "The selected judge model carries an auxiliary asset (projector, adapter or draft model). Judgings from such a model cannot be ranked; pick a plain text GGUF.");
        }
    }
}
