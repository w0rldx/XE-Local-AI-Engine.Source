namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>Which side of a comparison an evaluation scores.</summary>
public enum EvaluationTarget
{
    Undefined = 0,

    /// <summary>The untuned checkpoint the run started from — <c>TrainingRun.LinkedInstalledModelName</c>.</summary>
    Base = 1,

    /// <summary>What the run produced and promotion committed to the registry.</summary>
    Tuned = 2
}

/// <summary>
///     The frozen hold-out membership, persisted (encrypted) in <c>training_evaluation_runs.membership_json</c>. It
///     carries sample ids that select trajectories from the training run's encrypted, immutable corpus. The membership
///     does not duplicate sample content because the run-owned corpus is the replay source of truth.
/// </summary>
/// <remarks>
///     Both sides of a comparison take this membership from the SAME training run's freeze, which is the whole reason
///     their accuracies are comparable. <see cref="DatasetContentFingerprint" /> identifies the dataset version that
///     produced the corpus; later live-dataset edits do not change or invalidate that frozen replay.
/// </remarks>
public sealed record TrainingEvaluationMembershipV1
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The run whose freeze this membership was copied from.</summary>
    public Guid TrainingRunId { get; init; }

    /// <summary>Names the freeze inside that run, so two freezes of the same dataset stay distinguishable.</summary>
    public Guid FreezeId { get; init; }

    public Guid DatasetId { get; init; }

    public string DatasetContentFingerprint { get; init; } = string.Empty;

    /// <summary>The hold-out sample ids, in the run-owned corpus's frozen order. Scoring walks them in exactly this order.</summary>
    public IReadOnlyList<Guid> HoldoutSampleIds { get; init; } = [];
}

public sealed record TrainingEvaluationExecutionProvenanceV1
{
    public int SchemaVersion { get; init; } = 1;
    public required string Variant { get; init; }
    public required string ExecutableVersion { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string LaunchProjectionIdentity { get; init; }
    public int ContextTokens { get; init; }
    public int LaunchPolicyVersion { get; init; }
    public int LaunchPolicyChatCacheReuse { get; init; }
    public int LaunchPolicyChatCacheRamMiB { get; init; }
    public bool LaunchPolicySpeculativeDecoding { get; init; }
    public required string ModelSha256 { get; init; }
    public long ModelSizeBytes { get; init; }
    public string? AdapterSha256 { get; init; }
    public long? AdapterSizeBytes { get; init; }
}

/// <summary>What the operator asked to evaluate. The optional model name selects an installed base; tuned evaluation names a staged artifact.</summary>
public sealed record CreateEvaluationCommand(Guid TrainingRunId, EvaluationTarget Target, string? ModelNameOverride = null, Guid? ArtifactId = null);

/// <summary>What a comparison's create dialog needs to pre-fill itself from one training run.</summary>
public sealed record ComparisonSuggestion(
    Guid TrainingRunId,
    string? BaseModelName,
    string? TunedModelName,
    Guid? BaseEvaluationRunId,
    Guid? TunedEvaluationRunId,
    string? UnavailableReason);

/// <summary>
///     What one hold-out sample expects of the model. <see cref="ToolName" /> empty means the sample demonstrates a
///     no-tool answer, and the expectation inverts: the model passes by NOT calling anything.
/// </summary>
public sealed record EvaluationExpectation(string? ToolName, string? ArgumentsJson, string? ParameterSchema);

/// <summary>One tool call the model actually produced, flattened to the two things the scorer compares.</summary>
public sealed record EvaluationToolCall(string ToolName, string ArgumentsJson);

/// <summary>A refusal the evaluation surface reports as a 4xx rather than a fault. Message is operator-facing.</summary>
public sealed class EvaluationRejectedException : Exception
{
    public EvaluationRejectedException()
    {
    }

    public EvaluationRejectedException(string message)
        : base(message)
    {
    }

    public EvaluationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     A tool the model is OFFERED but can never execute. An evaluation is a single turn whose whole question is
///     "which call would the model make", so the offer needs a name and a parameter schema and nothing else; invoking
///     it would mean the evaluation had run somebody's tool, which is exactly what it must not do.
/// </summary>
internal sealed class DeclaredOnlyAIFunction : AIFunction
{
    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public DeclaredOnlyAIFunction(string name, string? description, string? parameterSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description ?? string.Empty;
        JsonSchema = ParseSchema(parameterSchema);
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema { get; }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The evaluation tool offer '{Name}' is declaration-only and is never executed.");

    private static JsonElement ParseSchema(string? parameterSchema)
    {
        if (string.IsNullOrWhiteSpace(parameterSchema))
        {
            return EmptyObjectSchema;
        }

        try
        {
            using var document = JsonDocument.Parse(parameterSchema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A snapshot this node can no longer parse still leaves a nameable offer; degrading to an open object is
            // better than dropping the tool and scoring every sample that expects it as a miss.
            return EmptyObjectSchema;
        }
    }
}
