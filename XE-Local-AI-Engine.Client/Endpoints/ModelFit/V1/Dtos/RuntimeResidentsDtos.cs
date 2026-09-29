namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

/// <summary>The image and whisper daemons held in memory (<c>GET model-fit/runtime-residents</c>); no paths, ports or pids.</summary>
public sealed class RuntimeResidentsResponse
{
    public required IReadOnlyList<RuntimeResidentResponse> Items { get; init; }
}

/// <summary>One resident daemon of the image or transcription runtime.</summary>
public sealed class RuntimeResidentResponse
{
    public required RuntimeResidentKindDto Runtime { get; init; }

    /// <summary>The image model name or the whisper catalog id; null while starting or switching models.</summary>
    public string? ModelId { get; init; }

    public required RuntimeResidentStateDto State { get; init; }

    /// <summary>The whisper daemon's own backend; null for image rows.</summary>
    public TranscriptionBackendDto? Backend { get; init; }

    /// <summary>False while that runtime's eject endpoint would answer 409.</summary>
    public required bool CanEject { get; init; }
}

/// <summary>Which runtime holds the resident.</summary>
public enum RuntimeResidentKindDto
{
    [JsonStringEnumMemberName("image")]
    Image = 0,

    [JsonStringEnumMemberName("transcription")]
    Transcription = 1
}

/// <summary>What the resident daemon is doing.</summary>
public enum RuntimeResidentStateDto
{
    [JsonStringEnumMemberName("starting")]
    Starting = 0,

    [JsonStringEnumMemberName("idle")]
    Idle = 1,

    [JsonStringEnumMemberName("active")]
    Active = 2,

    [JsonStringEnumMemberName("exited")]
    Exited = 3
}
