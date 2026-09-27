namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;

public static class BenchmarkJudgePolicyValidator
{
    private static readonly SearchValues<char> CriterionIdCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    ///     Validates a judge policy: the structural rules always, and the version constants this build supports when
    ///     <paramref name="strictVersions" /> is set.
    /// </summary>
    /// <remarks>
    ///     A version constant moving must never make an already-stored revision unreadable: bumping
    ///     <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> made `GET benchmarks/projects/{id}` 500, the whole
    ///     project header vanished from the UI, and it took the re-save control that heals the revision with it.
    ///     Everything else checked here is structural and holds for any row this build could have written.
    /// </remarks>
    /// <param name="strictVersions">
    ///     <see langword="true" /> on WRITE and immediately before EXECUTION: the policy must carry the versions this
    ///     build supports. <see langword="false" /> on READ.
    /// </param>
    public static void Validate(BenchmarkJudgePolicyV1 policy, bool strictVersions = true)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Model is null
            || string.IsNullOrWhiteSpace(policy.Model.ModelName)
            || string.IsNullOrWhiteSpace(policy.Model.ModelContentFingerprint)
            || policy.Model.MemberHashes is null)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.ModelInvalid, "The judge policy model identity is incomplete.");
        }

        if (policy.RequestedContextTokens <= 0)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.ContextTokensInvalid, "The judge policy context must be greater than zero.");
        }

        if (strictVersions && policy.PromptVersion != BenchmarkJudgePolicyVersions.PromptVersion)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.PromptVersionUnsupported, "The judge policy prompt version is unsupported.");
        }

        if (strictVersions && policy.OutputSchemaVersion != BenchmarkJudgePolicyVersions.OutputSchemaVersion)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.OutputSchemaVersionUnsupported, "The judge policy output schema version is unsupported.");
        }

        if (policy.Sampling is null)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.SamplingMissing, "The judge policy sampling configuration is missing.");
        }

        if (policy.ReferenceAnswer is { Length: > BenchmarkJudgePolicyVersions.MaximumReferenceAnswerLength })
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.ReferenceAnswerTooLong, "The judge policy reference answer is too long.");
        }

        ValidateMode(policy, strictVersions);
        ValidateRubric(policy.Rubric, strictVersions);
    }

    /// <summary>The judging mode, and the pairwise versions that ride with it.</summary>
    /// <remarks>
    ///     Checked on WRITE and EXECUTION only: a stored blob must still READ, or the constant moving would take the
    ///     project header down with it — the same rule the prompt/output-schema versions live under.
    /// </remarks>
    private static void ValidateMode(BenchmarkJudgePolicyV1 policy, bool strictVersions)
    {
        if (!strictVersions)
        {
            return;
        }

        var mode = BenchmarkJudgePolicyModes.Normalize(policy.Mode);
        if (mode is not (BenchmarkJudgePolicyModes.Pointwise or BenchmarkJudgePolicyModes.Pairwise))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.ModeUnsupported, "The judge policy mode is not supported.");
        }

        if (policy.PairwisePromptVersion != BenchmarkJudgePolicyVersions.PairwisePromptVersion
            || policy.PairwiseOutputSchemaVersion != BenchmarkJudgePolicyVersions.PairwiseOutputSchemaVersion)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.PairwiseVersionUnsupported, "The judge policy pairwise version is unsupported.");
        }
    }

    /// <summary>Whether every version this policy carries is one this build still writes and executes under.</summary>
    public static bool VersionsAreCurrent(BenchmarkJudgePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.PromptVersion == BenchmarkJudgePolicyVersions.PromptVersion
               && policy.OutputSchemaVersion == BenchmarkJudgePolicyVersions.OutputSchemaVersion
               && policy.Rubric?.Version == BenchmarkJudgePolicyVersions.RubricVersion;
    }

    /// <inheritdoc cref="Validate(BenchmarkJudgePolicyV1, bool)" />
    public static void ValidateRubric(BenchmarkJudgeRubricV1 rubric, bool strictVersions = true)
    {
        if (rubric?.Criteria is null)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.RubricMissing, "The judge policy rubric is missing.");
        }

        if (strictVersions && rubric.Version != BenchmarkJudgePolicyVersions.RubricVersion)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.RubricVersionUnsupported, "The judge policy rubric version is unsupported.");
        }

        if (rubric.Criteria.Count is < BenchmarkJudgePolicyVersions.MinimumCriterionCount or > BenchmarkJudgePolicyVersions.MaximumCriterionCount)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionCountOutOfRange,
                $"A judge rubric must carry {BenchmarkJudgePolicyVersions.MinimumCriterionCount} to {BenchmarkJudgePolicyVersions.MaximumCriterionCount} criteria.");
        }

        var seen = new HashSet<string>(rubric.Criteria.Count, StringComparer.Ordinal);
        foreach (var criterion in rubric.Criteria)
        {
            if (criterion is null || !IsValidCriterionId(criterion.Id))
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionIdInvalid, "A judge rubric criterion identifier is invalid.");
            }

            if (!seen.Add(criterion.Id))
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionIdDuplicate, "A judge rubric criterion identifier is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(criterion.Title) || criterion.Title.Length > BenchmarkJudgePolicyVersions.MaximumCriterionTitleLength)
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionTitleInvalid, "A judge rubric criterion title is invalid.");
            }

            if (string.IsNullOrWhiteSpace(criterion.Description) || criterion.Description.Length > BenchmarkJudgePolicyVersions.MaximumCriterionDescriptionLength)
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionDescriptionInvalid, "A judge rubric criterion description is invalid.");
            }

            if (criterion.Weight is < BenchmarkJudgePolicyVersions.MinimumCriterionWeight or > BenchmarkJudgePolicyVersions.MaximumCriterionWeight)
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionWeightOutOfRange,
                    $"A judge rubric criterion weight must be between {BenchmarkJudgePolicyVersions.MinimumCriterionWeight} and {BenchmarkJudgePolicyVersions.MaximumCriterionWeight}.");
            }

            // Verifiable configuration is parsed HERE, at activation, by the same parser the executor uses: a config the validator waves through is a judging that fails at run time with the GPU
            // already reserved. A failed judging must not be converted into score 0, so the operator's only signal would be a failed attempt. Strict path only: a stored revision must stay readable.
            if (strictVersions)
            {
                _ = BenchmarkJudgeVerifierConfig.Parse(criterion.Kind, criterion.Config);
            }
        }
    }

    private static bool IsValidCriterionId(string? id) =>
        id is { Length: > 0 and <= BenchmarkJudgePolicyVersions.MaximumCriterionIdLength }
        && id.AsSpan().IndexOfAnyExcept(CriterionIdCharacters) < 0;

    private static BenchmarkJudgePolicyValidationException Invalid(string code, string message) =>
        new(code, message);
}
