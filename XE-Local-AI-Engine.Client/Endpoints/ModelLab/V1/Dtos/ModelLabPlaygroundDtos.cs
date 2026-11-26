namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>One training-stepper run's knobs. Every knob is required; the bounds are <see cref="ModelLabPlaygroundSpec" />'s.</summary>
public sealed class StartModelLabPlaygroundRunRequest
{
    public required ModelLabPlaygroundDataset Dataset { get; init; }

    public required double LearningRate { get; init; }

    /// <summary>Each block visit adds its output to its input instead of replacing it.</summary>
    public required bool Residual { get; init; }

    /// <summary>One block's parameters serve every visit, instead of one block per visit.</summary>
    public required bool SharedBlock { get; init; }

    /// <summary>How many block visits run between the embedding and the head.</summary>
    public required int Depth { get; init; }

    public required int Hidden { get; init; }

    public required int Steps { get; init; }

    public required int Seed { get; init; }
}

public sealed class ModelLabPlaygroundRunRouteRequest
{
    public Guid RunId { get; init; }
}

public sealed class ModelLabPlaygroundConfigResponse
{
    public required ModelLabPlaygroundDataset Dataset { get; init; }

    public required double LearningRate { get; init; }

    public required bool Residual { get; init; }

    public required bool SharedBlock { get; init; }

    public required int Depth { get; init; }

    public required int Hidden { get; init; }

    public required int Steps { get; init; }

    public required int Seed { get; init; }
}

/// <summary><c>finished</c>, or <c>diverged</c> at <see cref="AtStep" />; a divergence is a finding, not a failure.</summary>
public sealed class ModelLabPlaygroundOutcomeResponse
{
    public required string Kind { get; init; }

    public required int? AtStep { get; init; }

    public required int UpdatesApplied { get; init; }
}

public sealed class ModelLabPlaygroundRunResponse
{
    public required Guid Id { get; init; }

    public required string Template { get; init; }

    public required ModelLabPlaygroundConfigResponse Config { get; init; }

    public required ModelLabPlaygroundOutcomeResponse Outcome { get; init; }

    /// <summary>The mean loss after the last applied update; null when it was not finite.</summary>
    public required double? FinalLoss { get; init; }

    public required double? FinalAccuracy { get; init; }

    public required long TraceByteCount { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class ModelLabPlaygroundRunListResponse
{
    /// <summary>False on a platform with no training runtime. The page shows <see cref="UnsupportedMessage" /> where a run would start.</summary>
    public required bool PlatformSupported { get; init; }

    public required string? UnsupportedMessage { get; init; }

    public required int MaxRetainedRuns { get; init; }

    public required IReadOnlyList<ModelLabPlaygroundRunResponse> Runs { get; init; }
}

/// <summary>One run and its recorded trace, passed through as the script wrote it.</summary>
public sealed class ModelLabPlaygroundRunDetailResponse
{
    public required ModelLabPlaygroundRunResponse Run { get; init; }

    public required JsonElement Trace { get; init; }
}

/// <summary>A run refused, failed or unreadable. <see cref="Reason" /> is a stable code; the message is what to do next.</summary>
public sealed class ModelLabPlaygroundBlockedResponse
{
    public required ModelLabPlaygroundRefusal Reason { get; init; }

    public required string Message { get; init; }
}
