namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One catalogue row, annotated with what this node knows about it.</summary>
/// <param name="Entry">The static catalogue row.</param>
/// <param name="Installed">Whether the weight file is present on disk.</param>
/// <param name="Download">The latest sanitized download status, when one is tracked.</param>
public sealed record TranscriptionModelView(WhisperModelEntry Entry, bool Installed, WhisperModelDownloadStatus? Download);

/// <summary>The model catalogue as this node sees it, with the selected and recommended ids resolved.</summary>
/// <param name="Models">Every catalogue row, ordered as the catalogue orders them.</param>
/// <param name="SelectedModelId">The operator's explicit choice, or <see langword="null" /> when none was made.</param>
/// <param name="RecommendedModelId">The hardware-fit recommendation; always a real catalogue id.</param>
public sealed record TranscriptionModelCatalogView(
    IReadOnlyList<TranscriptionModelView> Models,
    string? SelectedModelId,
    string RecommendedModelId);

/// <summary>The whole runtime picture the operator UI renders in one call.</summary>
/// <param name="Enabled">Whether the transcription feature is switched on for this node.</param>
/// <param name="Runtime">The supervisor's sanitized snapshot.</param>
/// <param name="Activity">What is currently holding the runtime.</param>
/// <param name="ManagedRuntime">The managed source-build record, when one exists.</param>
/// <param name="SelectedModelId">The operator's explicit model choice, or <see langword="null" />.</param>
/// <param name="RecommendedModelId">The hardware-fit recommendation.</param>
/// <param name="IdleTimeoutMinutes">The effective idle time-to-live in minutes.</param>
/// <param name="VadInstalled">
///     Whether the pinned voice-activity-detection weights are on disk. The daemon is launched with voice-activity
///     detection on, so a spawn fails while this is <see langword="false" />; surfacing it here is what lets the
///     operator see an incomplete installation rather than only a failed start.
/// </param>
public sealed record TranscriptionRuntimeView(
    bool Enabled,
    WhisperRuntimeStatusSnapshot Runtime,
    WhisperRuntimeActivitySnapshot Activity,
    WhisperInstalledRuntimeState? ManagedRuntime,
    string? SelectedModelId,
    string RecommendedModelId,
    int IdleTimeoutMinutes,
    bool VadInstalled);

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
