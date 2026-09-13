namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>Projects the store's decrypted session views onto the wire DTOs.</summary>
internal static class TranscriptionMapper
{
    // The same web-cased options the service serialized the config with. Default JsonSerializerOptions against a
    // web-cased document deserializes into a zeroed record without complaining, so the two must not drift.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    public static TranscriptionSessionSummaryResponse ToResponse(this TranscriptionSessionSummaryView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TranscriptionSessionSummaryResponse
        {
            Id = view.Id,
            Title = view.Title,
            Status = view.Status.ToString(),
            SourceKind = view.SourceKind.ToString(),
            ModelId = view.ModelId,
            DetectedLanguage = view.DetectedLanguage,
            DurationMs = view.DurationMs,
            SegmentCount = view.SegmentCount,
            CreatedAtUtc = view.CreatedAtUtc,
            UpdatedAtUtc = view.UpdatedAtUtc
        };
    }

    /// <summary>
    ///     Projects one session with its transcript.
    /// </summary>
    /// <param name="view">The decrypted session.</param>
    /// <param name="fallbackErrorCode">
    ///     The reason to report when the session row carries none. A refusal the service returns without writing the
    ///     row — <c>already-transcribing</c> — would otherwise come back as a 200 with a silent, unexplained session.
    /// </param>
    /// <param name="fallbackErrorMessage">The display-safe explanation that goes with <paramref name="fallbackErrorCode" />.</param>
    public static TranscriptionSessionDetailResponse ToResponse(this TranscriptionSessionDetailView view,
        string? fallbackErrorCode = null,
        string? fallbackErrorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TranscriptionSessionDetailResponse
        {
            Session = new TranscriptionSessionSummaryResponse
            {
                Id = view.Id,
                Title = view.Title,
                Status = view.Status.ToString(),
                SourceKind = view.SourceKind.ToString(),
                ModelId = view.ModelId,
                DetectedLanguage = view.DetectedLanguage,
                DurationMs = view.DurationMs,
                SegmentCount = view.SegmentCount,
                CreatedAtUtc = view.CreatedAtUtc,
                UpdatedAtUtc = view.UpdatedAtUtc
            },
            Segments = [.. view.Segments.Select(static segment => segment.ToResponse())],
            Config = ToResponse(view.ConfigJson),
            ErrorCode = view.ErrorCode ?? fallbackErrorCode,
            ErrorMessage = view.ErrorMessage ?? fallbackErrorMessage
        };
    }

    public static TranscriptSegmentResponse ToResponse(this TranscriptSegmentView segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new TranscriptSegmentResponse
        {
            Id = segment.Id,
            Seq = segment.Seq,
            StartMs = segment.StartMs,
            EndMs = segment.EndMs,
            Text = segment.Text,
            Channel = segment.Channel.ToString(),
            Confidence = segment.Confidence
        };
    }

    private static TranscriptionSessionConfigResponse ToResponse(string configJson)
    {
        // A config column that cannot be read must not turn a readable transcript into a 500: the transcript is the
        // thing the operator came for, and the options are metadata beside it.
        var config = TryDeserialize(configJson) ?? new TranscriptionSessionConfig { LanguageMode = "auto" };
        return new TranscriptionSessionConfigResponse
        {
            LanguageMode = config.LanguageMode,
            LanguageOverride = config.LanguageOverride,
            Translate = config.Translate,
            MaxWindowSeconds = config.MaxWindowSeconds,
            ChannelAttribution = config.ChannelAttribution
        };
    }

    private static TranscriptionSessionConfig? TryDeserialize(string configJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscriptionSessionConfig>(configJson, ConfigJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
