namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Why an image job is <see cref="ImageJobStatus.Failed" />: a closed vocabulary the UI translates, with
///     <c>SanitizedError</c> kept as the English text for older rows and API clients. Persisted as the member name.
/// </summary>
public enum ImageJobFailureReason
{
    /// <summary>A failure no narrower reason covers.</summary>
    Generic,

    /// <summary>The running image runtime build does not support the requested edit mode.</summary>
    EditModeUnsupported,

    /// <summary>The image server process died under the generation; the next generation restarts it.</summary>
    ProcessExited,

    /// <summary>The GPU ran out of memory.</summary>
    OutOfMemory,

    /// <summary>The loaded image-model cap is full and every loaded model is busy.</summary>
    LoadedModelCapReached,

    /// <summary>The source image of an edit job was deleted while the job waited.</summary>
    SourceImageMissing,

    /// <summary>A training run held the GPU when the job was about to start.</summary>
    TrainingRunning,

    /// <summary>A chat model was still generating when the job was about to start.</summary>
    ChatModelBusy,

    /// <summary>An application shutdown interrupted the job; startup reconciliation marked it failed.</summary>
    Interrupted
}
