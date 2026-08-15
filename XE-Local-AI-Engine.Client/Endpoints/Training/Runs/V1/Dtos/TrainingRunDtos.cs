namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

/// <summary>Hyper-parameters as the wire carries them. Omitted entirely means "use the computed defaults".</summary>
public sealed class TrainingRunOptionsPayload
{
    public required int MaxSeqLength { get; init; }
    public required int LoraR { get; init; }
    public required int LoraAlpha { get; init; }
    public required double LoraDropout { get; init; }
    public required int PerDeviceTrainBatchSize { get; init; }
    public required int GradientAccumulationSteps { get; init; }
    public required double LearningRate { get; init; }
    public required double WarmupRatio { get; init; }
    public required int Epochs { get; init; }
    public required int Seed { get; init; }
    public required string Optimizer { get; init; }
}

public sealed class CreateTrainingRunRequest
{
    public required Guid DatasetId { get; init; }

    /// <summary>The dataset version the wizard inspected. A sample edit since then refuses the run rather than training on a surprise.</summary>
    public required long ExpectedDatasetVersion { get; init; }

    public required Guid BaseArtifactId { get; init; }

    /// <summary>The operator's explicit acknowledgement of the base checkpoint's licensing. Required.</summary>
    public required bool LicenseConfirmed { get; init; }

    public TrainingRunOptionsPayload? Options { get; init; }
}

public sealed class TrainingRunByIdRequest
{
    public required Guid RunId { get; init; }
}

public sealed class ListTrainingRunsRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public Guid? DatasetId { get; init; }
}

public sealed class TrainingRunDefaultsRequest
{
    public required Guid BaseArtifactId { get; init; }

    /// <summary>Optional: the dataset the wizard has selected. Reserved for dataset-aware sizing; not read today.</summary>
    public Guid? DatasetId { get; init; }
}

public sealed class TrainingRunProgressResponse
{
    public required string Phase { get; init; }
    public required int Step { get; init; }
    public required int TotalSteps { get; init; }
    public double? Epoch { get; init; }
    public double? Loss { get; init; }
    public double? LearningRate { get; init; }
    public long? VramBytes { get; init; }
}

public sealed class TrainingRunResponse
{
    public required Guid Id { get; init; }
    public required Guid DatasetId { get; init; }
    public required Guid BaseArtifactId { get; init; }
    public required string Status { get; init; }
    public required int DatasetRevision { get; init; }
    public required string DatasetContentFingerprint { get; init; }
    public string? WorkStatus { get; init; }
    public string? ErrorMessage { get; init; }
    public string? LogTail { get; init; }
    public TrainingRunProgressResponse? Progress { get; init; }
    public TrainingRunOptionsPayload? Options { get; init; }
    public required long Version { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListTrainingRunsResponse
{
    public required IReadOnlyList<TrainingRunResponse> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

public sealed class TrainingRunFootprintResponse
{
    public required long GpuBytes { get; init; }
    public required long RamBytes { get; init; }
    public required long ParameterCount { get; init; }
    public required long TrainableParameterCount { get; init; }

    /// <summary>True beyond the size this feature has been exercised at. Advisory, not a refusal.</summary>
    public required bool Experimental { get; init; }
}

public sealed class TrainingRunLicenseResponse
{
    public required string RepoId { get; init; }
    public string? License { get; init; }
    public required bool IsGated { get; init; }

    /// <summary>False when no license metadata was found at all — still a confirmable fact, just a different one.</summary>
    public required bool MetadataPresent { get; init; }

    /// <summary>The exact wording the operator confirms. Its hash is recorded on the run.</summary>
    public required string ConfirmationText { get; init; }
}

public sealed class TrainingRunDefaultsResponse
{
    public required TrainingRunOptionsPayload Options { get; init; }
    public required TrainingRunFootprintResponse Estimate { get; init; }
    public required long AvailableVramBytes { get; init; }
    public required bool VramKnown { get; init; }
    public required bool Fits { get; init; }
    public string? RejectionReason { get; init; }
    public TrainingRunLicenseResponse? License { get; init; }
}

public sealed class TrainingRunBlockedResponse
{
    public required string Reason { get; init; }
    public required string Message { get; init; }
}
