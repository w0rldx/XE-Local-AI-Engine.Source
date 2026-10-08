namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>One piece of in-flight work an update restart would stop.</summary>
public sealed class AppUpdateBusyItem
{
    /// <summary>What kind of work is running.</summary>
    public required AppUpdateBusyKind Kind { get; init; }

    /// <summary>The model, session or build name when the tracking service knows one; otherwise null.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>The kinds of in-flight work the apply busy check reports.</summary>
public enum AppUpdateBusyKind
{
    TrainingRun,
    EvaluationRun,
    TrainingExport,
    ModelDownload,
    ImageModelDownload,
    TranscriptionModelDownload,
    LlamaCppSourceBuild,
    WhisperCppSourceBuild,
    StableDiffusionCppSourceBuild,
    Invocation,
    WorkSession
}
