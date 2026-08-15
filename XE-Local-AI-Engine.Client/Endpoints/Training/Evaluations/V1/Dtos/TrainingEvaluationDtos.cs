namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class CreateEvaluationRequest
{
    public required Guid TrainingRunId { get; init; }

    /// <summary>Which side of the comparison to score: the run's base model, or what it produced.</summary>
    public required EvaluationTarget Target { get; init; }

    /// <summary>Overrides the model the target implies — for an artifact promoted under a custom registry name.</summary>
    public string? ModelName { get; init; }
}

public sealed class EvaluationByIdRequest
{
    public required Guid EvaluationId { get; init; }
}

/// <summary>
///     The id is route-bound and the version comes from the body; neither can be <c>required</c>, because the body
///     is deserialized before the route value is applied and the generated client sends only <c>expectedVersion</c>.
/// </summary>
public sealed class DeleteEvaluationRequest
{
    public Guid EvaluationId { get; init; }

    public long ExpectedVersion { get; init; }
}

public sealed class ListEvaluationsRequest
{
    public Guid? TrainingRunId { get; init; }
}

public sealed class EvaluationKindTallyResponse
{
    public required string Kind { get; init; }
    public required int Total { get; init; }
    public required int Passed { get; init; }
}

public sealed class EvaluationResponse
{
    public required Guid Id { get; init; }
    public Guid? TrainingRunId { get; init; }

    /// <summary>Set once a comparison report binds this evaluation; a bound evaluation cannot be deleted.</summary>
    public Guid? ComparisonId { get; init; }

    public required string ModelName { get; init; }
    public string? ModelContentFingerprint { get; init; }
    public required Guid DatasetId { get; init; }

    /// <summary>
    ///     The dataset content fingerprint the evaluation's hold-out membership was frozen against. Differs from the
    ///     dataset's current fingerprint once the dataset was edited after the freeze.
    /// </summary>
    public required string DatasetContentFingerprint { get; init; }

    public required string Status { get; init; }
    public string? WorkStatus { get; init; }
    public required int TotalCount { get; init; }
    public required int ScoredCount { get; init; }
    public required int PassedCount { get; init; }
    public required IReadOnlyList<EvaluationKindTallyResponse> PerKind { get; init; }
    public string? ErrorMessage { get; init; }
    public required long Version { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListEvaluationsResponse
{
    public required IReadOnlyList<EvaluationResponse> Items { get; init; }
}
