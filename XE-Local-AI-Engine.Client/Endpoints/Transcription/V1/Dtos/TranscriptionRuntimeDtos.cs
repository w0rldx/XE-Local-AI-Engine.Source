namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using System.Text.Json.Serialization;

/// <summary>The acceleration backend the transcription runtime is built for.</summary>
public enum TranscriptionBackendDto
{
    [JsonStringEnumMemberName("cpu")]
    Cpu = 0,

    [JsonStringEnumMemberName("cuda")]
    Cuda = 1
}

/// <summary>Where the resolved <c>whisper-server</c> binary came from.</summary>
public enum TranscriptionBinarySourceDto
{
    [JsonStringEnumMemberName("pinned")]
    Pinned = 0,

    [JsonStringEnumMemberName("managed")]
    Managed = 1,

    [JsonStringEnumMemberName("byo")]
    BringYourOwn = 2
}

/// <summary>Lifecycle state of the resident daemon.</summary>
public enum TranscriptionRuntimeStateDto
{
    [JsonStringEnumMemberName("stopped")]
    Stopped = 0,

    [JsonStringEnumMemberName("starting")]
    Starting = 1,

    [JsonStringEnumMemberName("ready")]
    Ready = 2
}

/// <summary>Which repository a managed source build compiles from.</summary>
public enum TranscriptionSourceSelectionDto
{
    [JsonStringEnumMemberName("official")]
    Official = 0,

    [JsonStringEnumMemberName("custom")]
    Custom = 1
}

/// <summary>Which revision of the selected repository a managed source build compiles.</summary>
public enum TranscriptionSourceRevisionModeDto
{
    [JsonStringEnumMemberName("enginePinned")]
    EnginePinned = 0,

    [JsonStringEnumMemberName("defaultBranch")]
    DefaultBranch = 1,

    [JsonStringEnumMemberName("explicitCommit")]
    ExplicitCommit = 2
}

/// <summary>Validation state of the managed-runtime record.</summary>
public enum TranscriptionInstalledRuntimeValidityDto
{
    [JsonStringEnumMemberName("active")]
    Active = 0,

    [JsonStringEnumMemberName("invalid")]
    Invalid = 1
}

/// <summary>Explicit action body used by generated clients for otherwise body-less transcription POST actions.</summary>
public sealed class TranscriptionRuntimeActionRequest
{
    /// <summary>
    ///     Optional transport placeholder. Generated clients send an explicit JSON object for these otherwise
    ///     body-less POST actions; the server neither requires nor interprets the value.
    /// </summary>
    public bool? Accepted { get; init; }
}

/// <summary>What is currently holding the transcription runtime.</summary>
public sealed class TranscriptionRuntimeActivityResponse
{
    public required int ActiveTranscriptionCount { get; init; }

    public required int SpawnReadinessCount { get; init; }

    public required int ResidentProcessCount { get; init; }

    public required bool MutationReserved { get; init; }

    public required bool EvictionReserved { get; init; }

    public required bool IsBusy { get; init; }
}

/// <summary>
///     The managed source-build record, when this node has one. Present on the contract from the first slice and
///     always <see langword="null" /> until the managed build lane lands — the field exists so the shape does not
///     change under the generated client later.
/// </summary>
public sealed class WhisperInstalledRuntimeResponse
{
    public required TranscriptionInstalledRuntimeValidityDto Validity { get; init; }

    public required TranscriptionBackendDto DesiredBackend { get; init; }

    public required string SourceRepository { get; init; }

    public required string SourceCommit { get; init; }

    public required TranscriptionSourceSelectionDto SourceSelection { get; init; }

    public required TranscriptionSourceRevisionModeDto SourceRevisionMode { get; init; }

    public string? SourceRequestedCommit { get; init; }

    public required long InstalledAtUtc { get; init; }

    /// <summary>Why the record is a tombstone; present only for an invalid one.</summary>
    public string? InvalidReason { get; init; }
}

/// <summary>The whole runtime picture the operator UI renders.</summary>
public sealed class TranscriptionRuntimeStatusResponse
{
    /// <summary>Whether transcription is switched on for this node.</summary>
    public required bool Enabled { get; init; }

    public required TranscriptionRuntimeStateDto State { get; init; }

    /// <summary>The resolved backend, or <see langword="null" /> before anything has been resolved.</summary>
    public TranscriptionBackendDto? Backend { get; init; }

    public TranscriptionBinarySourceDto? BinarySource { get; init; }

    /// <summary>The pinned release tag, the managed build's source commit, or <c>byo</c>.</summary>
    public string? BinaryVersion { get; init; }

    public string? LoadedModelId { get; init; }

    /// <summary>The operator's explicit choice; <see langword="null" /> means the recommendation is used.</summary>
    public string? SelectedModelId { get; init; }

    public required string RecommendedModelId { get; init; }

    /// <summary>
    ///     Whether <c>ffmpeg</c> is on this node's PATH. It describes an ENGINE capability — transcoding the
    ///     containers the daemon cannot decode natively — and never a daemon flag: the daemon is deliberately never
    ///     launched with conversion on.
    /// </summary>
    public required bool SupportsTranscode { get; init; }

    public required int IdleTimeoutMinutes { get; init; }

    /// <summary>
    ///     Whether the pinned voice-activity-detection weights are installed.
    /// </summary>
    /// <remarks>
    ///     The daemon always launches with voice-activity detection on, so a node reporting <see langword="false" />
    ///     here cannot start the runtime at all — the one thing a "failed to start" message would not tell the
    ///     operator.
    /// </remarks>
    public required bool VadInstalled { get; init; }

    /// <summary>
    ///     Whether this node can capture the audio of a single application: Windows only, and only at or above the
    ///     build Microsoft documents for process loopback.
    /// </summary>
    /// <remarks>
    ///     The SPA hides the per-application source entirely when it is <see langword="false" />, rather than offering
    ///     an option that cannot work.
    /// </remarks>
    public required bool ProcessCaptureSupported { get; init; }

    public WhisperInstalledRuntimeResponse? ManagedRuntime { get; init; }

    public required TranscriptionRuntimeActivityResponse Activity { get; init; }
}

/// <summary>The 409 body every blocked transcription-runtime mutation returns.</summary>
public sealed class TranscriptionRuntimeBlockedResponse
{
    /// <summary>The reason code the SPA matches, so it can say "wait for the running work" rather than "error".</summary>
    public required string Reason { get; init; }

    public required string Message { get; init; }

    public required TranscriptionRuntimeActivityResponse Activity { get; init; }
}

/// <summary>The hardware-fit recommendation, with the numbers behind it.</summary>
public sealed class TranscriptionModelRecommendationResponse
{
    public required string RecommendedModelId { get; init; }

    public required string Tier { get; init; }

    public required long ApproximateVramBytes { get; init; }

    public required long ApproximateRamBytes { get; init; }

    /// <summary>The backend the recommendation was sized for.</summary>
    public required TranscriptionBackendDto Backend { get; init; }
}
