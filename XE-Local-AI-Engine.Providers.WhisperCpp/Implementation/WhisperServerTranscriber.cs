namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     The one adapter that speaks whisper-server's transcription wire protocol. Everything the daemon understands —
///     the route, the multipart field names, the response shape — stops here; callers see only
///     <see cref="WhisperTranscriptionRequest" /> and <see cref="WhisperTranscriptionResult" />.
/// </summary>
/// <remarks>
///     The audio is streamed straight from the caller's handle into the multipart body: nothing is written to disk and nothing the
///     caller owns is disposed. The submitted file name is a fixed placeholder rather than the user's, because the daemon prints the
///     received file name to its log. The request holds a transcription lease for its whole duration, which is what makes an eject, a
///     managed source build or a source-build remove answer <c>409 runtime-busy</c> while audio is being transcribed.
/// </remarks>
internal sealed class WhisperServerTranscriber : IWhisperTranscriber
{
    private const string InferenceRoute = "inference";

    /// <summary>Never the caller's file name: the daemon prints what it receives, and file names are user content.</summary>
    private const string SubmittedFileName = "audio.wav";

    private static readonly JsonSerializerOptions ResponseSerializerOptions = new(JsonSerializerDefaults.Web);

    // Enough of the daemon's error text to name the failure in a log line, never a whole HTML page.
    private const int LoggedErrorBodyChars = 300;

    private readonly HttpClient _httpClient;
    private readonly ILogger<WhisperServerTranscriber> _logger;
    private readonly WhisperRuntimeOptions _options;
    private readonly IWhisperServerSupervisor _supervisor;

    // ponytail: one latch per transcriber instance, so a node that runs for weeks warns about silent windows once in its
    // life. Move to a per-session or time-windowed latch if the first warning scrolling away ever hides a regression.
    private int _silentWindowFallbackWarned;

    public WhisperServerTranscriber(IWhisperServerSupervisor supervisor,
        HttpClient httpClient,
        WhisperRuntimeOptions options,
        ILogger<WhisperServerTranscriber>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<WhisperServerTranscriber>.Instance;
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
    }

    /// <inheritdoc />
    public TimeSpan InferenceTimeout => _options.InferenceTimeout;

    /// <inheritdoc />
    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Audio.CanSeek)
        {
            throw new ArgumentException("The transcription audio stream must be seekable so the request can declare its length.", nameof(request));
        }

        if (request.LanguageMode == WhisperLanguageMode.Explicit && string.IsNullOrWhiteSpace(request.LanguageCode))
        {
            throw new ArgumentException("An explicit language mode requires a language code.", nameof(request));
        }

        // Ensure-then-lease is two steps, and the daemon is mutable, so another caller can complete a model switch
        // between them. One retry covers that; a second failure surfaces rather than spinning through a switch storm.
        var (endpoint, lease) = await EnsureLeasedAsync(modelId, ct).ConfigureAwait(false);

        using (lease)
        {
            // A forced language is never detected, whatever the caller asked: there is nothing to learn from the probabilities.
            var languageProbabilities = request.DetectLanguage && request.LanguageMode != WhisperLanguageMode.Explicit;
            var result = await PostInferenceAsync(endpoint, request, languageProbabilities, ct).ConfigureAwait(false);
            lease.Touch();
            return result;
        }
    }

    private async Task<(WhisperServerEndpoint Endpoint, IWhisperTranscriptionLease Lease)> EnsureLeasedAsync(string modelId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = await _supervisor.EnsureRunningAsync(modelId, ct).ConfigureAwait(false);
            if (_supervisor.TryAcquireTranscriptionLease(endpoint.ModelId, endpoint.Generation) is { } lease)
            {
                return (endpoint, lease);
            }
        }

        throw new WhisperRuntimeException("The transcription runtime changed models while the request was starting.");
    }

    /// <summary>Posts one window to the daemon and maps its verbose JSON onto the contract.</summary>
    /// <remarks>
    ///     A request that asked for language probabilities and was answered 500 is retried ONCE, on the same lease, without
    ///     them. whisper-server (pinned 927cfce, launched with <c>--vad</c>) throws "basic_string: construction from null is
    ///     not valid" when VAD finds no speech segment AND probabilities are requested; the same audio answers 200 without
    ///     them. The retry's result carries no detected language, so a caller that wanted one asks again on a later window.
    /// </remarks>
    private async Task<WhisperTranscriptionResult> PostInferenceAsync(WhisperServerEndpoint endpoint,
        WhisperTranscriptionRequest request,
        bool languageProbabilities,
        CancellationToken ct)
    {
        // The client carries an infinite timeout on purpose — one client serves requests whose right budgets differ by four orders of magnitude — so every call site owns its own deadline, linked to
        // the caller's token so a caller cancellation still propagates unchanged.
        using var inferenceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inferenceCts.CancelAfter(_options.InferenceTimeout);

        request.Audio.Seek(offset: 0, SeekOrigin.Begin);

        using var content = BuildMultipartContent(request, languageProbabilities);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(new Uri(endpoint.BaseAddress, InferenceRoute), content, inferenceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WhisperRuntimeException(WhisperRuntimeException.InferenceTimedOutMessage);
        }
        catch (HttpRequestException exception)
        {
            // A daemon that crashed mid-request surfaces here as a connection reset; the supervisor turns that into "the process
            // exited" with its exit code, and logs the stderr tail.
            throw await _supervisor.ReportRequestFailureAsync(endpoint.Generation, exception, ct).ConfigureAwait(false)
                  ?? new WhisperRuntimeException("The transcription runtime could not be reached.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body carries the daemon's own error text; it is logged, never surfaced.
                var body = await response.Content.ReadAsStringAsync(inferenceCts.Token).ConfigureAwait(false);
                var loggedBody = body.Length > LoggedErrorBodyChars ? body[..LoggedErrorBodyChars] : body;

                if (languageProbabilities && response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    // Every silent window under auto-detect lands here until a language is learned, so only the first is a Warning.
                    if (Interlocked.Exchange(ref _silentWindowFallbackWarned, 1) == 0)
                    {
                        _logger.LogWarning("whisper-server answered 500 for a window without speech while language probabilities were requested; retrying without them. Later occurrences are logged at Debug. ({Body})",
                            loggedBody);
                    }
                    else
                    {
                        _logger.LogDebug("whisper-server answered 500 for a window without speech while language probabilities were requested. ({Body})", loggedBody);
                    }

                    _logger.LogDebug("Retrying the transcription request once without language probabilities.");
                    return await PostInferenceAsync(endpoint, request, languageProbabilities: false, ct).ConfigureAwait(false);
                }

                _logger.LogWarning("whisper-server rejected a transcription request ({StatusCode}): {Body}", (int)response.StatusCode, loggedBody);

                throw new WhisperRuntimeException("The transcription runtime rejected the audio.");
            }

            var payload = await response.Content.ReadFromJsonAsync<VerboseJsonResponse>(ResponseSerializerOptions, inferenceCts.Token).ConfigureAwait(false)
                          ?? throw new WhisperRuntimeException("The transcription runtime returned an empty result.");

            return MapResult(payload);
        }
    }

    private static MultipartFormDataContent BuildMultipartContent(WhisperTranscriptionRequest request, bool languageProbabilities)
    {
        var content = new MultipartFormDataContent();
        try
        {
#pragma warning disable CA2000 // MultipartFormDataContent takes ownership of every part it is given and disposes them
            // with itself; the caller disposes the multipart, and the catch below covers the only window in which a part could be orphaned — a failure partway through building it.
            // StreamContent also disposes the stream it was given, so the caller's handle is wrapped: this adapter never closes a stream the caller owns and deletes the temp file behind.
            var audio = new StreamContent(new NonDisposingStream(request.Audio));
            audio.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            content.Add(audio, "file", SubmittedFileName);

            content.Add(new StringContent("verbose_json"), "response_format");
            content.Add(new StringContent(request.LanguageMode == WhisperLanguageMode.Explicit ? request.LanguageCode! : "auto"), "language");
            content.Add(new StringContent(request.Translate ? "true" : "false"), "translate");
            content.Add(new StringContent(request.UseVoiceActivityDetection ? "true" : "false"), "vad");

            // The daemon is launched with language probabilities off because computing them is expensive; this is the
            // per-request switch that turns them back on for the one call that needs a detected language.
            content.Add(new StringContent(languageProbabilities ? "false" : "true"), "no_language_probabilities");
#pragma warning restore CA2000

            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    private static WhisperTranscriptionResult MapResult(VerboseJsonResponse payload)
    {
        var segments = payload.Segments is null
            ? []
            : payload.Segments
                     .Select(static segment => new WhisperTranscriptSegment
                     {
                         StartSeconds = segment.Start,
                         EndSeconds = segment.End,
                         Text = segment.Text?.Trim() ?? string.Empty,
                         Confidence = ToConfidence(segment.AvgLogprob)
                     })
                     .ToArray();

        return new WhisperTranscriptionResult
        {
            Text = payload.Text?.Trim() ?? string.Empty,
            Segments = segments,
            DetectedLanguageCode = ResolveDetectedLanguageCode(payload),
            DetectedLanguageProbability = payload.DetectedLanguageProbability,
            DurationSeconds = payload.Duration
        };
    }

    /// <summary>
    ///     The payload's <c>detected_language</c> is whisper's full English NAME for the language ("english"), not a
    ///     code.
    /// </summary>
    /// <remarks>
    ///     The only ISO codes in the response are the keys of the probability map, so the detected code is that map's
    ///     argmax — and is absent when the caller did not ask for detection.
    /// </remarks>
    private static string? ResolveDetectedLanguageCode(VerboseJsonResponse payload)
    {
        if (payload.LanguageProbabilities is not { Count: > 0 } probabilities)
        {
            return null;
        }

        string? best = null;
        var bestProbability = double.NegativeInfinity;
        foreach (var (code, probability) in probabilities)
        {
            if (probability > bestProbability)
            {
                bestProbability = probability;
                best = code;
            }
        }

        return best;
    }

    /// <summary>
    ///     The daemon reports no confidence field. The average log probability is the closest honest proxy, mapped
    ///     into 0..1; a missing or non-finite value yields null rather than a fabricated number.
    /// </summary>
    private static double? ToConfidence(double? averageLogProbability)
    {
        if (averageLogProbability is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        return Math.Clamp(Math.Exp(value), min: 0, max: 1);
    }

    /// <summary>
    ///     A read-only pass-through that swallows <see cref="Stream.Dispose(bool)" />. The multipart content owns and
    ///     disposes its parts; the caller owns the audio handle. This is the seam between those two ownerships.
    /// </summary>
    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() =>
            _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        // Dispose is deliberately NOT overridden. Stream's own teardown knows nothing about the wrapped handle, so inheriting it is exactly the required behaviour: the multipart content disposes this
        // wrapper and the caller's stream underneath it is left open, which is what the transcription contract promises.
    }

    /// <summary>The private shape of whisper-server's verbose JSON. Nothing of this type escapes the project.</summary>
    private sealed record VerboseJsonResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("duration")]
        public double Duration { get; init; }

        [JsonPropertyName("segments")]
        public IReadOnlyList<VerboseJsonSegment>? Segments { get; init; }

        [JsonPropertyName("detected_language_probability")]
        public double? DetectedLanguageProbability { get; init; }

        [JsonPropertyName("language_probabilities")]
        public IReadOnlyDictionary<string, double>? LanguageProbabilities { get; init; }
    }

    private sealed record VerboseJsonSegment
    {
        [JsonPropertyName("start")]
        public double Start { get; init; }

        [JsonPropertyName("end")]
        public double End { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("avg_logprob")]
        public double? AvgLogprob { get; init; }
    }
}
