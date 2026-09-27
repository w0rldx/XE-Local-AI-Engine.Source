namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;
using System.Text.Json.Serialization;

/// <summary>
/// The judge prompt and output-schema versions a v1 judge policy is allowed to carry. A future revision of either is a
/// conscious bump here plus a matching parser, never a silent widening.
/// </summary>
public static class BenchmarkJudgePolicyVersions
{
    /// <summary>3 since the length-neutrality sentence entered <c>BenchmarkJudgePromptV2.SystemPrompt</c>.</summary>
    /// <remarks>
    ///     The prompt TEXT is hashed nowhere, so this integer is the only thing that can separate two judgings across a
    ///     wording change: without the bump, verdicts taken either side of it share a policy revision and a cohort
    ///     generation and get dense-ranked against each other after having been asked different questions. A policy
    ///     still carrying 2 is rejected by <see cref="BenchmarkJudgePolicyValidationCodes.PromptVersionUnsupported" />,
    ///     which is the existing forced-re-judge path.
    /// </remarks>
    public const int PromptVersion = 3;

    public const int OutputSchemaVersion = 2;
    public const int RubricVersion = 1;

    /// <summary>The pairwise prompt and output-schema versions, kept beside the pointwise pair.</summary>
    /// <remarks>
    ///     Same reason: the prompt TEXT is hashed nowhere. They are POLICY MEMBERS rather than bare constants (orders 8
    ///     and 9), exactly as <see cref="PromptVersion" /> and <see cref="OutputSchemaVersion" /> are at orders 2 and
    ///     3, so a pairwise wording change lands inside <c>ComputePolicyHash</c> — and a pointwise prompt edit still
    ///     does not churn pairwise verdicts, nor the reverse.
    /// </remarks>
    public const int PairwisePromptVersion = 1;

    public const int PairwiseOutputSchemaVersion = 1;

    public const int MinimumCriterionCount = 1;
    public const int MaximumCriterionCount = 8;
    public const int MinimumCriterionWeight = 1;
    public const int MaximumCriterionWeight = 100;
    public const int MaximumCriterionIdLength = 32;
    public const int MaximumCriterionTitleLength = 64;
    public const int MaximumCriterionDescriptionLength = 1024;
    public const int MaximumReferenceAnswerLength = 32768;
}

// Every record below is serialized into the policy hash, so each one pins its property order explicitly with JsonPropertyOrder:
// reordering a declaration must never silently change a hash, and the policy must never inherit the declaration order of a type it does not own.
/// <summary>One weighted rubric criterion, as the policy hash sees it.</summary>
/// <remarks>
///     <see cref="BenchmarkJudgeCriterionKinds.Llm" /> is the legacy-compatible default for a criterion carrying no
///     explicit kind; every other kind is checked server-side by <see cref="BenchmarkJudgeVerifiers" />.
///     <paramref name="Config" /> is canonicalized by <see cref="BenchmarkJudgePolicyCanonicalizer" /> so two
///     operators who typed the same rules in a different key order hash identically.
/// </remarks>
/// <param name="Kind">How this criterion is decided: the judge model, or a server-side verifier with no inference at all.</param>
/// <param name="Config">The kind's configuration as canonical JSON, or <see langword="null" /> for <c>llm</c>.</param>
public sealed record BenchmarkJudgeRubricCriterionV1(
    [property: JsonPropertyOrder(0)]
    string Id,
    [property: JsonPropertyOrder(1)]
    string Title,
    [property: JsonPropertyOrder(2)]
    string Description,
    [property: JsonPropertyOrder(3)]
    int Weight,
    [property: JsonPropertyOrder(4)]
    string Kind = BenchmarkJudgeCriterionKinds.Llm,
    [property: JsonPropertyOrder(5)]
    string? Config = null);

public sealed record BenchmarkJudgeRubricV1(
    [property: JsonPropertyOrder(0)]
    int Version,
    [property: JsonPropertyOrder(1)]
    IReadOnlyList<BenchmarkJudgeRubricCriterionV1> Criteria);

/// <summary>The judge model identity a policy hashes over.</summary>
/// <remarks>
/// Deliberately narrower than <see cref="BenchmarkInstalledModelSnapshotV1"/>: only fields whose change actually
/// changes a score belong in the policy hash, so a registry-alias or provider-mapping churn does not spawn a spurious
/// policy revision.
/// </remarks>
public sealed record BenchmarkJudgePolicyModelV1(
    [property: JsonPropertyOrder(0)]
    string ModelName,
    [property: JsonPropertyOrder(1)]
    string ModelContentFingerprint,
    [property: JsonPropertyOrder(2)]
    IReadOnlyList<string> MemberHashes)
{
    public static BenchmarkJudgePolicyModelV1 FromSnapshot(BenchmarkInstalledModelSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var members = snapshot.Members.Select(static member => member.Sha256).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new BenchmarkJudgePolicyModelV1(snapshot.ModelName, snapshot.ModelContentFingerprint, members);
    }
}

/// <summary>
/// The policy's own projection of <see cref="BenchmarkSamplingSnapshotV1"/>. The shared snapshot is not hashed directly:
/// it belongs to the runtime snapshot contract, and a reorder there must not move a judge policy hash.
/// </summary>
public sealed record BenchmarkJudgePolicySamplingV1(
    [property: JsonPropertyOrder(0)]
    float? Temperature,
    [property: JsonPropertyOrder(1)]
    float? TopP,
    [property: JsonPropertyOrder(2)]
    int? TopK,
    [property: JsonPropertyOrder(3)]
    float? MinP,
    [property: JsonPropertyOrder(4)]
    int? MaxOutputTokens,
    [property: JsonPropertyOrder(5)]
    float? RepeatPenalty,
    [property: JsonPropertyOrder(6)]
    int? RepeatLastN,
    [property: JsonPropertyOrder(7)]
    float? PresencePenalty,
    [property: JsonPropertyOrder(8)]
    float? FrequencyPenalty,
    [property: JsonPropertyOrder(9)]
    IReadOnlyList<string> Stop,
    [property: JsonPropertyOrder(10)]
    string SeedPolicy,
    [property: JsonPropertyOrder(11)]
    string? SeedValue)
{
    public static BenchmarkJudgePolicySamplingV1 FromSnapshot(BenchmarkSamplingSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new BenchmarkJudgePolicySamplingV1((float?)snapshot.Temperature,
            snapshot.TopP,
            snapshot.TopK,
            snapshot.MinP,
            snapshot.MaxOutputTokens,
            snapshot.RepeatPenalty,
            snapshot.RepeatLastN,
            snapshot.PresencePenalty,
            snapshot.FrequencyPenalty,
            snapshot.Stop,
            snapshot.SeedPolicy,
            snapshot.SeedValue);
    }
}

public sealed record BenchmarkJudgePolicyV1(
    [property: JsonPropertyOrder(0)]
    BenchmarkJudgePolicyModelV1 Model,
    [property: JsonPropertyOrder(1)]
    int RequestedContextTokens,
    [property: JsonPropertyOrder(2)]
    int PromptVersion,
    [property: JsonPropertyOrder(3)]
    int OutputSchemaVersion,
    [property: JsonPropertyOrder(4)]
    BenchmarkJudgePolicySamplingV1 Sampling,
    [property: JsonPropertyOrder(5)]
    BenchmarkJudgeRubricV1 Rubric,
    [property: JsonPropertyOrder(6)]
    string? ReferenceAnswer,
    [property: JsonPropertyOrder(7)]
    string Mode = BenchmarkJudgePolicyModes.Pointwise,
    [property: JsonPropertyOrder(8)]
    int PairwisePromptVersion = BenchmarkJudgePolicyVersions.PairwisePromptVersion,
    [property: JsonPropertyOrder(9)]
    int PairwiseOutputSchemaVersion = BenchmarkJudgePolicyVersions.PairwiseOutputSchemaVersion);

/// <summary>Stable codes carried by <see cref="BenchmarkJudgePolicyValidationException"/>; safe to map to an API error body.</summary>
public static class BenchmarkJudgePolicyValidationCodes
{
    public const string ModelInvalid = "judge-policy-model-invalid";
    public const string ContextTokensInvalid = "judge-policy-context-tokens-invalid";
    public const string PromptVersionUnsupported = "judge-policy-prompt-version-unsupported";
    public const string OutputSchemaVersionUnsupported = "judge-policy-output-schema-version-unsupported";
    public const string SamplingMissing = "judge-policy-sampling-missing";
    public const string RubricMissing = "judge-policy-rubric-missing";
    public const string RubricVersionUnsupported = "judge-policy-rubric-version-unsupported";
    public const string CriterionCountOutOfRange = "judge-policy-criterion-count-out-of-range";
    public const string CriterionIdInvalid = "judge-policy-criterion-id-invalid";
    public const string CriterionIdDuplicate = "judge-policy-criterion-id-duplicate";
    public const string CriterionTitleInvalid = "judge-policy-criterion-title-invalid";
    public const string CriterionDescriptionInvalid = "judge-policy-criterion-description-invalid";
    public const string CriterionWeightOutOfRange = "judge-policy-criterion-weight-out-of-range";
    public const string ReferenceAnswerTooLong = "judge-policy-reference-answer-too-long";
    public const string CriterionKindUnsupported = "judge-policy-criterion-kind-unsupported";
    public const string CriterionConfigInvalid = "judge-policy-criterion-config-invalid";
    public const string ModeUnsupported = "judge-policy-mode-unsupported";
    public const string PairwiseVersionUnsupported = "judge-policy-pairwise-version-unsupported";
}

public sealed class BenchmarkJudgePolicyValidationException : InvalidOperationException
{
    public BenchmarkJudgePolicyValidationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
