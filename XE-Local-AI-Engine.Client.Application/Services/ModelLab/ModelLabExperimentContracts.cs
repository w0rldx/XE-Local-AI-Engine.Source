namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json.Serialization;

/// <summary>What another service may ask about the running job. It is a live answer, not a row: a failed completion can leave a row that says Running with no job.</summary>
public interface IModelLabActiveJob
{
    Guid? ActiveBaseArtifactId { get; }
}

/// <summary>Bounds on one Model Lab job, each a separate failure.</summary>
public sealed class ModelLabJobOptions
{
    /// <summary>How long the script may stay silent. It beats every 20 seconds until the first token and while saving, and reports each token between.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the script gets to exit after a stop request or after closing its output, before its group is killed.</summary>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>What <c>modellab.py</c> reads from <c>job.json</c>. Holds the plaintext prompt, in the owner-only work directory.</summary>
public sealed record ModelLabTraceJobV1
{
    public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

    public required string CheckpointPath { get; init; }

    public required string Prompt { get; init; }

    public required string TracePath { get; init; }

    public required string ResultPath { get; init; }

    public required IReadOnlyList<int> Layers { get; init; }

    public int MaxNewTokens { get; init; } = ModelLabTraceSpec.MaxNewTokens;

    public int TraceByteCap { get; init; } = ModelLabTraceSpec.TraceByteCap;

    public int Seed { get; init; } = ModelLabTraceSpec.Seed;
}

public sealed record ModelLabTraceRequest
{
    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required bool LicenseConfirmed { get; init; }
}

/// <summary>Why a trace did not start. The value is the API's stable refusal code.</summary>
public enum ModelLabStartRefusal
{
    UnsupportedPlatform,
    CheckpointNotFound,
    CheckpointNotReady,
    LicenseNotConfirmed,
    PreflightRefused,
    RuntimeUnavailable,
    Busy,
    ModelLoaded,
    InsufficientMemory,
    ProcessNotConfirmedStopped
}

public sealed record ModelLabStart
{
    public Guid? ExperimentId { get; init; }

    public ModelLabStartRefusal? Refusal { get; init; }

    public string? RefusalMessage { get; init; }
}

public enum ModelLabTraceReadStatus
{
    Found,
    NotFound,
    Unavailable
}

/// <summary>One token at one layer, as measured. The short names are the file's, chosen to keep the capped trace small.</summary>
public sealed record ModelLabTraceRecord
{
    [JsonPropertyName("s")]
    public int Step { get; init; }

    [JsonPropertyName("l")]
    public int Layer { get; init; }

    [JsonPropertyName("k")]
    public string Kind { get; init; } = "?";

    /// <summary>Null, here and in the lists below, where the measured value was not a finite number.</summary>
    [JsonPropertyName("n")]
    public double? Norm { get; init; }

    /// <summary>The 1st, 50th and 99th percentiles of the residual's values.</summary>
    [JsonPropertyName("p")]
    public IReadOnlyList<double?> Percentiles { get; init; } = [];

    [JsonPropertyName("ti")]
    public IReadOnlyList<int> TopIndices { get; init; } = [];

    [JsonPropertyName("tv")]
    public IReadOnlyList<double?> TopValues { get; init; } = [];
}

public sealed record ModelLabTraceFooter
{
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("records")]
    public int Records { get; init; }

    [JsonPropertyName("dropped")]
    public int Dropped { get; init; }

    [JsonPropertyName("cap")]
    public long Cap { get; init; }
}

public sealed record ModelLabTrace
{
    public required ModelLabTraceReadStatus Status { get; init; }

    public IReadOnlyList<ModelLabTraceRecord> Records { get; init; } = [];

    public ModelLabTraceFooter? Footer { get; init; }
}

/// <summary>One generated token. The text is what the tokenizer decodes the id to on its own.</summary>
public sealed record ModelLabResultToken
{
    public int Id { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>What <c>modellab.py</c> measured, from <c>result.json</c>.</summary>
public sealed record ModelLabTraceResult
{
    public int PromptTokens { get; init; }

    public IReadOnlyList<ModelLabResultToken> Tokens { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public double Seconds { get; init; }

    public long PeakVramBytes { get; init; }

    public int BlockCount { get; init; }

    public string? ModelClass { get; init; }

    public string? Device { get; init; }

    public string? Torch { get; init; }

    public string? Transformers { get; init; }

    public string? Python { get; init; }
}
