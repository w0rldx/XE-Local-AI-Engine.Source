namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One catalogue row, annotated with what this node knows about it.</summary>
public sealed class TranscriptionModelView
{
    /// <summary>The static catalogue row.</summary>
    public required WhisperModelEntry Entry { get; init; }

    /// <summary>Whether the weight file is present on disk.</summary>
    public required bool Installed { get; init; }

    /// <summary>The latest sanitized download status, when one is tracked.</summary>
    public required WhisperModelDownloadStatus? Download { get; init; }
}

/// <summary>The model catalogue as this node sees it, with the selected and recommended ids resolved.</summary>
public sealed record TranscriptionModelCatalogView
{
    /// <summary>Every catalogue row, ordered as the catalogue orders them.</summary>
    public required IReadOnlyList<TranscriptionModelView> Models { get; init; }

    /// <summary>The operator's explicit choice, or <see langword="null" /> when none was made.</summary>
    public required string? SelectedModelId { get; init; }

    /// <summary>The hardware-fit recommendation; always a real catalogue id.</summary>
    public required string RecommendedModelId { get; init; }
}

/// <summary>The whole runtime picture the operator UI renders in one call.</summary>
public sealed record TranscriptionRuntimeView
{
    /// <summary>Whether the transcription feature is switched on for this node.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The supervisor's sanitized snapshot.</summary>
    public required WhisperRuntimeStatusSnapshot Runtime { get; init; }

    /// <summary>What is currently holding the runtime.</summary>
    public required WhisperRuntimeActivitySnapshot Activity { get; init; }

    /// <summary>The managed source-build record, when one exists.</summary>
    public required WhisperInstalledRuntimeState? ManagedRuntime { get; init; }

    /// <summary>The operator's explicit model choice, or <see langword="null" />.</summary>
    public required string? SelectedModelId { get; init; }

    /// <summary>The hardware-fit recommendation.</summary>
    public required string RecommendedModelId { get; init; }

    /// <summary>The effective idle time-to-live in minutes.</summary>
    public required int IdleTimeoutMinutes { get; init; }

    /// <summary>Whether the pinned voice-activity-detection weights are on disk.</summary>
    /// <remarks>
    ///     The daemon is launched with voice-activity detection on, so a spawn fails while this is
    ///     <see langword="false" />; surfacing it here is what lets the operator see an incomplete installation rather
    ///     than only a failed start.
    /// </remarks>
    public required bool VadInstalled { get; init; }

    public required bool ProcessCaptureSupported { get; init; }
}

/// <summary>
///     The application-layer facade the transcription endpoints call. It composes the supervisor, the catalogue, the
///     node settings and the hardware profile so no endpoint has to know how those fit together.
/// </summary>
public interface ITranscriptionRuntimeService
{
    /// <summary>The full runtime picture, including the recommendation and the managed-runtime record.</summary>
    Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct);

    /// <summary>Ejects the resident daemon, or reports the activity that blocked the attempt.</summary>
    Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct);

    /// <summary>The catalogue with installed flags, download statuses, and the selected and recommended ids.</summary>
    Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct);

    /// <summary>The hardware-fit recommendation for the backend this node would actually use.</summary>
    Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct);

    /// <summary>
    ///     Persists the operator's model choice. A <see langword="null" /> or blank id clears the override, so the
    ///     node falls back to the recommendation.
    /// </summary>
    /// <exception cref="ArgumentException">A non-blank id that is not in the catalogue.</exception>
    Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct);

    /// <summary>
    ///     The model this node would actually load: the operator's choice when it is set and still valid, otherwise
    ///     the recommendation.
    /// </summary>
    Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct);
}
