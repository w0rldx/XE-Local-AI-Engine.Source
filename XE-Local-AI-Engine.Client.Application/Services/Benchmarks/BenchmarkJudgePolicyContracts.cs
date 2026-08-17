namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The judge prompt and output-schema versions a v1 judge policy is allowed to carry. A future revision of either is a
/// conscious bump here plus a matching parser, never a silent widening.
/// </summary>
public static class BenchmarkJudgePolicyVersions
{
    /// <summary>
    ///     3 since the length-neutrality sentence entered <c>BenchmarkJudgePromptV2.SystemPrompt</c>. The prompt TEXT
    ///     is hashed nowhere, so this integer is the only thing that can separate two judgings across a wording change:
    ///     without the bump, verdicts taken either side of it share a policy revision and a cohort generation and get
    ///     dense-ranked against each other after having been asked different questions. A policy still carrying 2 is
    ///     rejected by <see cref="BenchmarkJudgePolicyValidationCodes.PromptVersionUnsupported" />, which is the
    ///     existing forced-re-judge path.
    /// </summary>
    public const int PromptVersion = 3;

    public const int OutputSchemaVersion = 2;
    public const int RubricVersion = 1;

    public const int MinimumCriterionCount = 1;
    public const int MaximumCriterionCount = 8;
    public const int MinimumCriterionWeight = 1;
    public const int MaximumCriterionWeight = 100;
    public const int MaximumCriterionIdLength = 32;
    public const int MaximumCriterionTitleLength = 64;
    public const int MaximumCriterionDescriptionLength = 1024;
    public const int MaximumReferenceAnswerLength = 32768;
}

// Every record below is serialized into the policy hash, so each one pins its property order explicitly with
// JsonPropertyOrder. Reordering a declaration must never silently change a hash, and the policy must never inherit the
// declaration order of a type it does not own.
public sealed record BenchmarkJudgeRubricCriterionV1(
    [property: JsonPropertyOrder(0)]
    string Id,
    [property: JsonPropertyOrder(1)]
    string Title,
    [property: JsonPropertyOrder(2)]
    string Description,
    [property: JsonPropertyOrder(3)]
    int Weight);

public sealed record BenchmarkJudgeRubricV1(
    [property: JsonPropertyOrder(0)]
    int Version,
    [property: JsonPropertyOrder(1)]
    IReadOnlyList<BenchmarkJudgeRubricCriterionV1> Criteria);

/// <summary>
/// The judge model identity a policy hashes over. Deliberately narrower than <see cref="BenchmarkInstalledModelSnapshotV1"/>:
/// only fields whose change actually changes a score belong in the policy hash, so a registry-alias or provider-mapping
/// churn does not spawn a spurious policy revision.
/// </summary>
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
        return new BenchmarkJudgePolicySamplingV1(snapshot.Temperature,
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
    string? ReferenceAnswer);

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
}

public sealed class BenchmarkJudgePolicyValidationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class BenchmarkJudgePolicyValidator
{
    private static readonly SearchValues<char> CriterionIdCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <param name="strictVersions">
    ///     <see langword="true" /> on WRITE and immediately before EXECUTION: the policy must carry the versions this
    ///     build supports. <see langword="false" /> on READ — a version constant moving must never make an
    ///     already-stored revision unreadable. It did once: bumping <see cref="BenchmarkJudgePolicyVersions.PromptVersion" />
    ///     made `GET benchmarks/projects/{id}` 500, the whole project header vanished from the UI, and it took the
    ///     re-save control that heals the revision with it. Everything else checked here is structural and holds for
    ///     any row this build could have written.
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

        ValidateRubric(policy.Rubric, strictVersions);
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
        }
    }

    private static bool IsValidCriterionId(string? id) =>
        id is { Length: > 0 and <= BenchmarkJudgePolicyVersions.MaximumCriterionIdLength }
        && id.AsSpan().IndexOfAnyExcept(CriterionIdCharacters) < 0;

    private static BenchmarkJudgePolicyValidationException Invalid(string code, string message) =>
        new(code, message);
}

/// <summary>
/// Deterministic canonical form of a judge policy. Two policies that differ only in the order the operator entered the
/// criteria hash identically; any field change does not.
/// </summary>
public static class BenchmarkJudgePolicyCanonicalizer
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string ToCanonicalJson(BenchmarkJudgePolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);
        return JsonSerializer.Serialize(Normalize(policy), CanonicalOptions);
    }

    private static void Validate(BenchmarkJudgePolicyV1 policy)
    {
        if (policy.Model?.MemberHashes is null || policy.Rubric?.Criteria is null)
        {
            throw new ArgumentException("A judge policy must carry a model identity and a rubric to be canonicalised.", nameof(policy));
        }
    }

    public static string ComputePolicyHash(BenchmarkJudgePolicyV1 policy) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson(policy))));

    private static BenchmarkJudgePolicyV1 Normalize(BenchmarkJudgePolicyV1 policy)
    {
        return policy with
        {
            Model = policy.Model with
            {
                MemberHashes = [.. policy.Model.MemberHashes.Order(StringComparer.Ordinal)]
            },
            Rubric = policy.Rubric with
            {
                Criteria = [.. policy.Rubric.Criteria.OrderBy(static criterion => criterion.Id, StringComparer.Ordinal)]
            }
        };
    }
}

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
