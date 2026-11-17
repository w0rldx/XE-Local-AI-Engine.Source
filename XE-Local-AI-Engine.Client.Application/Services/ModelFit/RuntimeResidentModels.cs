namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>Which non-llama.cpp runtime holds a resident.</summary>
public enum RuntimeResidentKind
{
    Image = 0,
    Transcription = 1,

    /// <summary>The warm Model Lab Python worker; its model id is the checkpoint's repository id.</summary>
    ModelLab = 2
}

/// <summary>What a resident daemon is doing right now.</summary>
public enum RuntimeResidentState
{
    Starting = 0,
    Idle = 1,
    Active = 2,
    Exited = 3
}

/// <summary>One image or whisper daemon or the warm Model Lab worker held in memory, read from in-memory state only.</summary>
public sealed class RuntimeResident
{
    public required RuntimeResidentKind Runtime { get; init; }

    /// <summary>The image model name, the whisper catalog id or the Model Lab checkpoint's repository id; <see langword="null" /> while starting or switching.</summary>
    public required string? ModelId { get; init; }

    public required RuntimeResidentState State { get; init; }

    /// <summary>The whisper daemon's own backend; always <see langword="null" /> for image rows.</summary>
    public required WhisperBackend? Backend { get; init; }

    /// <summary>Whether that runtime's eject would be admitted now: the same gate fields its eviction reservation refuses on.</summary>
    public required bool CanEject { get; init; }
}
