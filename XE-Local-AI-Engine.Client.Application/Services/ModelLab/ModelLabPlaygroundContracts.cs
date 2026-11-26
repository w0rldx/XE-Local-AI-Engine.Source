namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What the training stepper's one template accepts and stores. <c>nnplayground.py</c> enforces the same bounds again.</summary>
public static class ModelLabPlaygroundSpec
{
    /// <summary>The <c>nnplayground.py</c> protocol, separate from the tracer's <see cref="ModelLabTraceSpec.ContractVersion" />.</summary>
    public const int ContractVersion = 1;

    /// <summary>The <c>formatVersion</c> of <c>trace.json</c> and <c>result.json</c>.</summary>
    public const int FormatVersion = 1;

    public const string JobKind = "playground";

    public const string TemplateId = "tiny-mlp-v1";

    public const int MaxTemplateIdLength = 32;

    public const double MinLearningRate = 0.0001;

    public const double MaxLearningRate = 10;

    public const int MinDepth = 1;

    public const int MaxDepth = 4;

    public const int MinHidden = 2;

    public const int MaxHidden = 16;

    public const int MinSteps = 1;

    public const int MaxSteps = 2000;

    public const int MinSeed = 0;

    public const int MaxSeed = int.MaxValue;

    public const int MaxTraceBytes = 4 * 1024 * 1024;

    public const int MaxResultBytes = 16 * 1024;

    /// <summary>Saved runs beyond these, oldest first, are trimmed when a run is saved.</summary>
    public const int MaxRetainedRuns = 20;
}

/// <summary>The datasets the template trains on. The one vocabulary the request, the OpenAPI enum and the job file share.</summary>
public enum ModelLabPlaygroundDataset
{
    [JsonStringEnumMemberName("xor")] Xor,
    [JsonStringEnumMemberName("spirals")] Spirals
}

/// <summary>The knobs of one run, as the job file and <c>result.json</c> carry them. Read back from the stored result, so nothing is <c>required</c>.</summary>
public sealed class ModelLabPlaygroundConfigV1
{
    public ModelLabPlaygroundDataset Dataset { get; init; }

    public double LearningRate { get; init; }

    public bool Residual { get; init; }

    public bool SharedBlock { get; init; }

    public int Depth { get; init; }

    public int Hidden { get; init; }

    public int Steps { get; init; }

    public int Seed { get; init; }
}

/// <summary>The <c>job.json</c> the host writes for <c>nnplayground.py</c>. The output paths sit inside the job's own directory.</summary>
public sealed class ModelLabPlaygroundJobV1
{
    public int ContractVersion { get; init; } = ModelLabPlaygroundSpec.ContractVersion;

    public string Kind { get; init; } = ModelLabPlaygroundSpec.JobKind;

    public string Template { get; init; } = ModelLabPlaygroundSpec.TemplateId;

    public required ModelLabPlaygroundConfigV1 Config { get; init; }

    public required string TracePath { get; init; }

    public required string ResultPath { get; init; }

    public int MaxTraceBytes { get; init; } = ModelLabPlaygroundSpec.MaxTraceBytes;

    public int MaxResultBytes { get; init; } = ModelLabPlaygroundSpec.MaxResultBytes;
}

/// <summary>How a run ended as a finding: <c>finished</c>, or <c>diverged</c> at <see cref="AtStep" /> after <see cref="UpdatesApplied" /> updates.</summary>
public sealed class ModelLabPlaygroundOutcomeV1
{
    public string Kind { get; init; } = string.Empty;

    public int? AtStep { get; init; }

    public int UpdatesApplied { get; init; }
}

/// <summary><c>result.json</c>, stored encrypted as the run's summary. A non-finite loss or accuracy is null.</summary>
public sealed class ModelLabPlaygroundResultV1
{
    public int FormatVersion { get; init; }

    public string Template { get; init; } = string.Empty;

    public ModelLabPlaygroundConfigV1 Config { get; init; } = new();

    public ModelLabPlaygroundOutcomeV1 Outcome { get; init; } = new();

    public double? FinalLoss { get; init; }

    public double? FinalAccuracy { get; init; }
}

/// <summary>Why a run was not started, failed, or cannot be read. The value is the API's stable refusal code.</summary>
public enum ModelLabPlaygroundRefusal
{
    UnsupportedPlatform,
    RuntimeUnavailable,
    Busy,

    /// <summary>The script failed, was stopped, or wrote outputs that fail their checks. Nothing was saved.</summary>
    RunFailed,

    /// <summary>The saved trace is missing or fails its integrity check. The row can still be deleted.</summary>
    TraceUnavailable
}

/// <summary>One saved run as the list and the start answer show it.</summary>
public sealed class ModelLabPlaygroundRunSummary
{
    public required Guid Id { get; init; }

    public required string TemplateId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required long TraceByteCount { get; init; }

    public required ModelLabPlaygroundResultV1 Result { get; init; }
}

/// <summary>A start's answer: the saved run, or the refusal that saved nothing.</summary>
public sealed class ModelLabPlaygroundOutcome
{
    public ModelLabPlaygroundRunSummary? Run { get; init; }

    public ModelLabPlaygroundRefusal? Refusal { get; init; }

    public string? RefusalMessage { get; init; }
}

/// <summary>One run with its trace, or <see cref="ModelLabPlaygroundRefusal.TraceUnavailable" /> when the stored trace cannot be read.</summary>
public sealed class ModelLabPlaygroundRunRead
{
    public required ModelLabPlaygroundRunSummary Run { get; init; }

    /// <summary>The trace's UTF-8 JSON, or null when it is unavailable.</summary>
    public ReadOnlyMemory<byte>? Trace { get; init; }

    public string? UnavailableMessage { get; init; }
}

public sealed class ModelLabPlaygroundRunNotFoundException : ResourceNotFoundException
{
    public ModelLabPlaygroundRunNotFoundException(Guid runId) : base($"Training stepper run '{runId}' was not found.")
    {
    }
}
