namespace XE_Local_AI_Engine.Client.Services.Transcription.Live;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One durable piece of transcript. Times are milliseconds from the start of the session's audio.</summary>
/// <param name="Channel">Which lane produced it.</param>
/// <param name="StartMs">Segment start in session audio time.</param>
/// <param name="EndMs">Segment end in session audio time.</param>
/// <param name="Text">The trimmed text.</param>
/// <param name="Confidence">The model's confidence, passed through untouched from the provider.</param>
public sealed record LiveCommit(TranscriptChannel Channel, long StartMs, long EndMs, string Text, double? Confidence);

/// <summary>What one call into a lane produced.</summary>
/// <param name="Partial">The provisional text after this call: the returned segments that are not yet durable.</param>
/// <param name="Commits">The segments that became durable during this call, in commit order.</param>
/// <param name="DetectedLanguage">
///     The language code this call learned, or <see langword="null" /> when it learned none. It is reported once,
///     on the call that first sees it; <see cref="LiveTranscriptionSegmenter.DetectedLanguageCode" /> holds it after.
/// </param>
public sealed record LiveTick(string Partial, IReadOnlyList<LiveCommit> Commits, string? DetectedLanguage);

/// <summary>
///     A lane stopped making progress: repeated submissions at the same boundary neither committed anything nor
///     consumed any audio, so continuing would loop forever.
/// </summary>
/// <remarks>
///     Failing is the point. The alternative — advancing the watermark past audio whose transcription never resolved —
///     silently deletes speech to protect a loop, which is worse than a session the operator can see failed.
/// </remarks>
public sealed class LiveSegmenterStalledException : Exception
{
    public LiveSegmenterStalledException()
        : base("The transcription lane stopped making progress.")
    {
    }

    public LiveSegmenterStalledException(string message)
        : base(message)
    {
    }

    public LiveSegmenterStalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     One live transcription lane: raw PCM in, provisional text and durable segments out.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything here is audio time.</b> The clock is <see cref="WavPcm16.BytesPerMillisecond" /> against the
///         cumulative count of bytes pushed, never a wall clock and never a per-frame sum — sixteen two-byte frames
///         carry one millisecond, and each <c>2 / 32</c> truncates to nothing, so an accumulated clock would never
///         advance and the lane would buffer forever. Re-deriving the time from the total makes the frame partition
///         irrelevant, which is the whole reason the hub may accept any even frame length.
///     </para>
///     <para>
///         <b>Duplicates are dropped by a watermark, never by comparing text.</b> Consecutive windows overlap, so the
///         model re-transcribes the same words at every boundary. A segment starting before
///         <see cref="CommittedEndMs" /> has already been said and is discarded; nothing here diffs or fuzzy-matches
///         strings.
///     </para>
///     <para>
///         <b>Audio is never cropped out of a window.</b> When the uncommitted span reaches the cap the whole span is
///         submitted and the tail guard is suspended for that submission, so no millisecond can pass the watermark
///         without having been offered to the transcriber at least once.
///     </para>
///     <para>
///         The lane is not thread-safe: its owner serializes calls per lane, because a second frame arriving during an
///         inference call must wait rather than race the buffer.
///     </para>
/// </remarks>
public sealed class LiveTranscriptionSegmenter
{
    private const int NoProgressSubmissionLimit = 2;

    private readonly TranscriptChannel _channel;
    private readonly string? _languageCode;
    private readonly int _maxWindowMs;
    private readonly string _modelId;
    private readonly LiveSegmenterSettings _settings;

    /// <summary>The uncommitted PCM, holding audio from <see cref="_tailStartMs" /> onwards.</summary>
    /// <remarks>
    ///     A list rather than a ring buffer: it holds at most the cap (ten seconds, 320 KB) and is compacted from the
    ///     front about once a second.
    ///     <c>ponytail: front-compaction copies the retained tail; a ring buffer only pays off far above 10 s windows.</c>
    /// </remarks>
    private readonly List<byte> _tail = [];

    private readonly bool _translate;
    private readonly IWhisperTranscriber _transcriber;
    private long _audioEndMs;
    private long _committedEndMs;
    private bool _detectLanguage = true;
    private string? _detectedLanguageCode;
    private long _lastSubmittedBoundaryMs = -1;
    private long _nextTickMs;
    private int _noProgressSubmissions;
    private string _partial = string.Empty;
    private long _tailStartMs;
    private long _totalPushedBytes;

    /// <summary>Creates a lane.</summary>
    /// <param name="transcriber">The only external dependency; the lane never talks to the runtime supervisor itself.</param>
    /// <param name="channel">Which side of the session this lane carries.</param>
    /// <param name="modelId">The whisper model every submission names.</param>
    /// <param name="languageCode">A forced language, or <see langword="null" /> to let the model detect one.</param>
    /// <param name="translate">Whether the model translates to English.</param>
    /// <param name="settings">The window, guard and tick sizes.</param>
    public LiveTranscriptionSegmenter(IWhisperTranscriber transcriber,
        TranscriptChannel channel,
        string modelId,
        string? languageCode,
        bool translate,
        LiveSegmenterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _transcriber = transcriber;
        _channel = channel;
        _modelId = modelId;
        _languageCode = string.IsNullOrWhiteSpace(languageCode) ? null : languageCode;
        _translate = translate;
        _settings = settings;
        _maxWindowMs = settings.MaxWindowSeconds * 1_000;
        _nextTickMs = settings.TickMs;
    }

    /// <summary>How much audio this lane has received, in milliseconds.</summary>
    public long AudioEndMs => _audioEndMs;

    /// <summary>The watermark: no segment starting before this is ever emitted again.</summary>
    public long CommittedEndMs => _committedEndMs;

    /// <summary>The first language code any window reported, or <see langword="null" /> while it is still unknown.</summary>
    public string? DetectedLanguageCode => _detectedLanguageCode;

    /// <summary>Accepts one frame of 16 kHz mono int16 PCM and runs whatever submissions its arrival makes due.</summary>
    /// <param name="pcm16">Raw little-endian samples. Any length is accepted, including one that is not a whole millisecond.</param>
    /// <param name="cancellationToken">Cancels an in-flight submission.</param>
    /// <exception cref="LiveSegmenterStalledException">Submissions stopped making progress.</exception>
    public async ValueTask<LiveTick> PushAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
    {
        var commits = new List<LiveCommit>();
        string? learnedLanguage = null;
        var remaining = pcm16;

        while (true)
        {
            var boundaryMs = Math.Min(_nextTickMs, _committedEndMs + _maxWindowMs);
            if (_audioEndMs >= boundaryMs)
            {
                if (boundaryMs == _lastSubmittedBoundaryMs)
                {
                    if (++_noProgressSubmissions >= NoProgressSubmissionLimit)
                    {
                        throw new LiveSegmenterStalledException(
                            $"The transcription lane made no progress across {NoProgressSubmissionLimit} submissions ending at {_audioEndMs} ms.");
                    }
                }
                else
                {
                    _lastSubmittedBoundaryMs = boundaryMs;
                    _noProgressSubmissions = 0;
                }

                learnedLanguage = await SubmitAsync(atCap: _audioEndMs - _tailStartMs >= _maxWindowMs,
                    flush: false,
                    commits,
                    cancellationToken).ConfigureAwait(false) ?? learnedLanguage;

                // Set from the caller and not inside the submission, so a submission that declines to run (an
                // ordinary tick with less than a tick of new audio) still moves the boundary and cannot spin.
                _nextTickMs = _audioEndMs + _settings.TickMs;
                continue;
            }

            if (remaining.IsEmpty)
            {
                break;
            }

            var takeBytes = (int)Math.Min(remaining.Length, (boundaryMs - _audioEndMs) * WavPcm16.BytesPerMillisecond);
            _tail.AddRange(remaining.Span[..takeBytes]);
            _totalPushedBytes += takeBytes;
            _audioEndMs = WavPcm16.DurationMs(_totalPushedBytes);
            remaining = remaining[takeBytes..];
        }

        return new LiveTick(_partial, commits, learnedLanguage);
    }

    /// <summary>Finalizes the lane: one last submission with the tail guard suspended, so the retained audio commits.</summary>
    /// <param name="cancellationToken">Cancels the submission.</param>
    /// <remarks>
    ///     It submits whatever is retained regardless of the tick size — a recording shorter than one tick would
    ///     otherwise never reach the model at all — and it clears nothing unless the submission succeeded, so a failed
    ///     call leaves the audio for a retry instead of turning one failed request into lost speech.
    /// </remarks>
    public async ValueTask<LiveTick> FlushAsync(CancellationToken cancellationToken)
    {
        var commits = new List<LiveCommit>();
        var learnedLanguage = await SubmitAsync(atCap: false, flush: true, commits, cancellationToken).ConfigureAwait(false);
        return new LiveTick(_partial, commits, learnedLanguage);
    }

    private async ValueTask<string?> SubmitAsync(bool atCap, bool flush, List<LiveCommit> commits, CancellationToken cancellationToken)
    {
        var windowStartMs = _tailStartMs;
        var uncommittedMs = _audioEndMs - windowStartMs;
        if (uncommittedMs <= 0)
        {
            return null;
        }

        // A tick that fires with less than a tick of new audio has nothing new to say. A cap or a flush submission
        // has no such minimum: the cap must clear the buffer, and a flush is the last chance this audio ever gets.
        if (!atCap && !flush && uncommittedMs < _settings.TickMs)
        {
            return null;
        }

        var windowEndMs = _audioEndMs;
        var wav = WavPcm16.Wrap(CollectionsMarshal.AsSpan(_tail)[..(int)(uncommittedMs * WavPcm16.BytesPerMillisecond)]);

        // A MemoryStream over a byte[] is seekable, so the multipart body can report a content length. It is written
        // read-only because the provider must not mutate it, and disposed here because the provider disposes nothing
        // it did not create.
        using var audio = new MemoryStream(wav, writable: false);
        var result = await _transcriber.TranscribeAsync(_modelId, new WhisperTranscriptionRequest
        {
            Audio = audio,
            ContentType = "audio/wav",
            LanguageMode = _languageCode is null ? WhisperLanguageMode.Auto : WhisperLanguageMode.Explicit,
            LanguageCode = _languageCode,
            Translate = _translate,
            UseVoiceActivityDetection = true,
            DetectLanguage = _detectLanguage
        }, cancellationToken).ConfigureAwait(false);

        string? learnedLanguage = null;
        if (_detectedLanguageCode is null && !string.IsNullOrWhiteSpace(result.DetectedLanguageCode))
        {
            // The probability pass is paid once per session, not once per second.
            _detectedLanguageCode = result.DetectedLanguageCode;
            _detectLanguage = false;
            learnedLanguage = result.DetectedLanguageCode;
        }

        Apply(result, windowStartMs, windowEndMs, atCap, flush, commits);
        return learnedLanguage;
    }

    /// <summary>Turns one transcription result into commits and moves the watermark. Nothing here awaits.</summary>
    private void Apply(WhisperTranscriptionResult result,
        long windowStartMs,
        long windowEndMs,
        bool atCap,
        bool flush,
        List<LiveCommit> commits)
    {
        // The guard is suspended when the buffer must be cleared anyway: at the cap and on the final flush. That
        // commits the model's answer about audio this lane cut itself, so a word straddling the boundary is a guess
        // from a fragment and is durable either way.
        // ponytail: one word per forced boundary is the accepted error (base inserts "to" at 5 s, large-v3-turbo
        // duplicates "you" at 7.1 s); carry keep_ms of the previous window into the next and de-duplicate on tokens,
        // as whisper.cpp's examples/stream/stream.cpp does, if that word ever costs more than the overlap decode.
        // Suspended means suspended, not "zero milliseconds of guard". A model routinely reports an end time a little
        // past the audio it was given — VAD padding does it, and the recorded fixture has a 2000 ms window answered
        // with a segment ending at 2020 ms — so a guard of zero still rejects those, and the caller then frees the
        // audio anyway. The text would be lost with nothing left to re-transcribe it from.
        var committableUntilMs = atCap || flush ? long.MaxValue : windowEndMs - _settings.TailGuardMs;

        // The one place seconds become milliseconds. Past this line the slice has no doubles and no "seconds".
        var segments = result.Segments
            .Select(segment => new
            {
                StartMs = (long)Math.Round(segment.StartSeconds * 1000, MidpointRounding.AwayFromZero) + windowStartMs,
                EndMs = (long)Math.Round(segment.EndSeconds * 1000, MidpointRounding.AwayFromZero) + windowStartMs,
                Text = segment.Text.Trim(),
                segment.Confidence
            })
            .OrderBy(segment => segment.StartMs)
            .ToList();

        var pending = new List<string>();
        var watermarkMs = _committedEndMs;
        var committedEndMs = -1L;
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (segment.StartMs < watermarkMs)
            {
                // Already said. The windows overlap, so the model repeats itself at every boundary; this clause is
                // the entire de-duplication mechanism and it never looks at the text.
                continue;
            }

            if (segment.EndMs > committableUntilMs)
            {
                pending.Add(segment.Text);
                continue;
            }

            commits.Add(new LiveCommit(_channel, segment.StartMs, segment.EndMs, segment.Text, segment.Confidence));
            watermarkMs = Math.Max(watermarkMs, segment.EndMs);
            committedEndMs = watermarkMs;
        }

        if (committedEndMs >= 0)
        {
            // Clamped to the audio that actually exists: a model that runs a segment past the end of the window must
            // not make the lane drop bytes it never received.
            AdvanceWatermark(Math.Clamp(committedEndMs, _tailStartMs, windowEndMs));
        }

        if (flush || (atCap && committedEndMs < 0))
        {
            // Nothing was found in this span, or this is the last word on it. Free the bytes as well as moving the
            // watermark: advancing one without the other retains the audio for the life of the session.
            AdvanceWatermark(windowEndMs);
        }

        _partial = flush ? string.Empty : string.Join(' ', pending).Trim();
    }

    private void AdvanceWatermark(long watermarkMs)
    {
        if (watermarkMs <= _tailStartMs)
        {
            _committedEndMs = Math.Max(_committedEndMs, watermarkMs);
            return;
        }

        _tail.RemoveRange(0, (int)((watermarkMs - _tailStartMs) * WavPcm16.BytesPerMillisecond));
        _tailStartMs = watermarkMs;
        _committedEndMs = watermarkMs;
    }
}
