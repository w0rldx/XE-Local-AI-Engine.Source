namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The R2 boundary adapter: everything whisper-server understands stops at this class, and everything the engine
///     consumes starts here. The daemon's own JSON shape and multipart field names appear ONLY in this file on the
///     test side — asserting the boundary is the one thing a test is allowed to know them for.
/// </summary>
/// <remarks>
///     The supervisor is a fake rather than the real one because the subject is the wire adapter, not the daemon's
///     lifecycle; the real supervisor would need a real child process to say anything here. The HTTP handler is a stub
///     for the same reason the rest of the suite uses one: it is the only way to pin a response shape byte for byte.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class WhisperServerTranscriberTests
{
    private const string RealisticPayload = """
                                            {
                                              "text": "  And so my fellow Americans.  ",
                                              "duration": 11.0,
                                              "detected_language": "english",
                                              "detected_language_probability": 0.97,
                                              "language_probabilities": { "de": 0.01, "en": 0.97, "fr": 0.02 },
                                              "segments": [
                                                { "start": 0.0, "end": 5.5, "text": "  And so my fellow  ", "avg_logprob": -0.2231435513 },
                                                { "start": 5.5, "end": 11.0, "text": " Americans. ", "avg_logprob": null }
                                              ]
                                            }
                                            """;

    [Test]
    public async Task Transcribe_MapsTheVerboseJsonPayloadOntoTheContract()
    {
        // The whole mapping table in one assertion block: trimmed text, seconds preserved, exp(avg_logprob) as the
        // confidence proxy, a null log probability yielding null rather than a fabricated number, and the detected
        // code taken from the probability map's argmax — the payload's own "detected_language" is an English NAME,
        // never a code, so reading it would put "english" on the wire as if it were ISO.
        await using var harness = new TranscriberHarness(RealisticPayload);

        var result = await harness.TranscribeAsync();

        AssertEx.Equal("And so my fellow Americans.", result.Text);
        AssertEx.Equal("en", result.DetectedLanguageCode);
        AssertClose(expected: 0.97, result.DetectedLanguageProbability, "detected-language probability");
        AssertClose(expected: 11.0, result.DurationSeconds, "duration");
        AssertEx.Equal(expected: 2, result.Segments.Count);

        var first = result.Segments[0];
        AssertClose(expected: 0.0, first.StartSeconds, "first segment start");
        AssertClose(expected: 5.5, first.EndSeconds, "first segment end");
        AssertEx.Equal("And so my fellow", first.Text);
        AssertClose(expected: 0.8, first.Confidence, "first segment confidence");

        var second = result.Segments[1];
        AssertEx.Equal("Americans.", second.Text);
        AssertEx.Null(second.Confidence, "A missing avg_logprob must not be turned into a confidence.");
    }

    [Test]
    [Arguments(0.0, 1.0)]
    [Arguments(1.5, 1.0)]
    [Arguments(-0.6931471805599453, 0.5)]
    public async Task Transcribe_ClampsTheDerivedConfidenceIntoTheUnitInterval(double averageLogProbability, double expected)
    {
        // A non-negative average log probability is not physical, but the daemon is the one reporting it; the contract
        // promises 0..1, so the clamp is what keeps a consumer from seeing 4.48 where it expects a probability.
        var payload = $$"""
                        {"text":"x","duration":1.0,"segments":[{"start":0,"end":1,"text":"x","avg_logprob":{{averageLogProbability.ToString(CultureInfo.InvariantCulture)}}}]}
                        """;
        await using var harness = new TranscriberHarness(payload);

        var result = await harness.TranscribeAsync();

        AssertClose(expected, result.Segments[0].Confidence, "clamped confidence");
    }

    [Test]
    public async Task Transcribe_WithoutLanguageProbabilities_ReportsNoDetectedCode()
    {
        // Computing the probabilities is expensive and is off unless the caller asks. Absent map means "not asked",
        // which must read as null rather than as a guess.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");

        var result = await harness.TranscribeAsync(detectLanguage: false);

        AssertEx.Null(result.DetectedLanguageCode);
        AssertEx.Empty(result.Segments);
    }

    [Test]
    public async Task Transcribe_SendsTheDaemonsExactMultipartFieldSet()
    {
        // The multipart contract, pinned: the fixed submitted file name (the daemon logs what it receives, and a file
        // name is user content), verbose_json so the timed segments exist at all, and the inverted
        // no_language_probabilities switch, which is "false" precisely when detection IS wanted.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");

        await harness.TranscribeAsync(detectLanguage: true);

        var body = AssertEx.NotNull(harness.Handler.LastRequestBody);
        AssertEx.Contains(body, "name=file", StringComparison.Ordinal);
        AssertEx.Contains(body, "filename=audio.wav", StringComparison.Ordinal);
        AssertEx.Contains(body, "name=response_format", StringComparison.Ordinal);
        AssertEx.Contains(body, "verbose_json", StringComparison.Ordinal);
        AssertEx.Contains(body, "name=no_language_probabilities", StringComparison.Ordinal);
        AssertEx.Contains(body, "name=vad", StringComparison.Ordinal);
        AssertEx.Contains(body, "name=translate", StringComparison.Ordinal);
        AssertEx.Contains(body, "name=language", StringComparison.Ordinal);

        // The route too: it is the one whisper-server path the engine ever reaches.
        AssertEx.Equal("/inference", AssertEx.NotNull(harness.Handler.LastRequestUri).AbsolutePath);
    }

    [Test]
    public async Task Transcribe_WithAnExplicitLanguage_SendsTheCodeInsteadOfAuto()
    {
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");

        await harness.TranscribeAsync(configure: static request => request with
        {
            LanguageMode = WhisperLanguageMode.Explicit,
            LanguageCode = "de"
        });

        var body = AssertEx.NotNull(harness.Handler.LastRequestBody);
        AssertEx.Contains(body, "name=language", StringComparison.Ordinal);
        AssertEx.Contains(body, "\r\n\r\nde\r\n", StringComparison.Ordinal);
    }

    [Test]
    public async Task Transcribe_NonSeekableAudio_ThrowsBeforeTouchingTheRuntime()
    {
        // A length-less body would be sent chunked, which the daemon's multipart parser is not verified against. The
        // adapter must refuse it here rather than discover it at the far end — and must not start a daemon to do so.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");
        await using var audio = new NonSeekableStream();

        await AssertEx.ThrowsAsync<ArgumentException>(() => harness.Transcriber.TranscribeAsync("base",
            new WhisperTranscriptionRequest
            {
                Audio = audio,
                ContentType = "audio/wav"
            },
            CancellationToken.None));

        AssertEx.Equal(expected: 0, harness.Supervisor.EnsureCallCount, "A rejected request must not start a daemon.");
        AssertEx.Equal(expected: 0, harness.Handler.CallCount);
    }

    [Test]
    public async Task Transcribe_ExplicitLanguageWithoutACode_ThrowsBeforeTouchingTheRuntime()
    {
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");

        await AssertEx.ThrowsAsync<ArgumentException>(() => harness.TranscribeAsync(configure: static request => request with
        {
            LanguageMode = WhisperLanguageMode.Explicit,
            LanguageCode = null
        }));

        AssertEx.Equal(expected: 0, harness.Supervisor.EnsureCallCount);
    }

    [Test]
    public async Task Transcribe_WhenTheGenerationMovesOnce_RetriesTheEnsureLeasePairAndSucceeds()
    {
        // Ensure-then-lease is two steps against a mutable daemon, so a model switch can land between them. One retry
        // covers that; without it an ordinary concurrent switch would fail a caller's request.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");
        harness.Supervisor.LeaseRefusals = 1;

        var result = await harness.TranscribeAsync();

        AssertEx.Equal("x", result.Text);
        AssertEx.Equal(expected: 2, harness.Supervisor.EnsureCallCount, "The retry must re-ensure, not reuse the stale endpoint.");
        AssertEx.Equal(expected: 2, harness.Supervisor.LeaseAttemptCount);
    }

    [Test]
    public async Task Transcribe_WhenTheGenerationKeepsMoving_FailsTypedAfterTheSecondAttempt()
    {
        // The other half of the retry rule: bounded at two, so a switch storm surfaces instead of spinning.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");
        harness.Supervisor.LeaseRefusals = int.MaxValue;

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync());

        AssertEx.Contains(exception.Message, "changed models", StringComparison.Ordinal);
        AssertEx.Equal(expected: 2, harness.Supervisor.LeaseAttemptCount, "The retry is bounded at two attempts.");
        AssertEx.Equal(expected: 0, harness.Handler.CallCount, "No audio may be posted without a lease.");
    }

    [Test]
    public async Task Transcribe_WhenTheDaemonRejectsTheAudio_ThrowsTypedAndReleasesTheLease()
    {
        // The daemon's own error body is user-facing only as a sanitized message, and the lease must not leak — an
        // undisposed lease would keep an eject answering 409 forever.
        await using var harness = new TranscriberHarness("""{"error":"bad audio"}""", HttpStatusCode.BadRequest);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync());

        AssertEx.Contains(exception.Message, "rejected the audio", StringComparison.Ordinal);
        AssertEx.False(exception.Message.Contains("bad audio", StringComparison.Ordinal),
            "The daemon's own error text must not reach the caller.");
        AssertEx.Equal(expected: 1, harness.Supervisor.LeasesDisposed);
    }

    // whisper-server 927cfce with --vad answers 500 ("basic_string: construction from null is not valid") when VAD finds
    // no speech AND language probabilities were asked for; the same audio is a 200 without them.
    [Test]
    public async Task Transcribe_DetectingALanguage_RetriesA500OnceWithoutProbabilitiesAndReportsNoLanguage()
    {
        await using var harness = new TranscriberHarness(Sequence((HttpStatusCode.InternalServerError, NullStringError),
            (HttpStatusCode.OK, SilentPayload)));

        var result = await harness.TranscribeAsync(detectLanguage: true);

        AssertEx.Equal(expected: 2, harness.Handler.CallCount);
        AssertEx.Equal(expected: 2, harness.Handler.RequestBodies.Count);
        AssertEx.Equal("false", NoLanguageProbabilities(harness.Handler.RequestBodies[0]), "The first request asks for probabilities.");
        AssertEx.Equal("true", NoLanguageProbabilities(harness.Handler.RequestBodies[1]), "The retry does not.");
        AssertEx.Contains(harness.Handler.RequestBodies[1], "RIFFxxxxWAVE", StringComparison.Ordinal);
        AssertEx.Null(result.DetectedLanguageCode, "A result without probabilities detects nothing; a later window asks again.");
        AssertEx.Empty(result.Segments);
        AssertEx.Equal(expected: 1, harness.Supervisor.LeasesDisposed);
    }

    // 60 s of silence under auto-detect is 60 fallbacks: the first is worth a Warning, the rest are not.
    [Test]
    public async Task Transcribe_SilentWindowFallback_WarnsOnceThenLogsAtDebug()
    {
        await using var harness = new TranscriberHarness(Sequence((HttpStatusCode.InternalServerError, NullStringError),
            (HttpStatusCode.OK, SilentPayload),
            (HttpStatusCode.InternalServerError, NullStringError),
            (HttpStatusCode.OK, SilentPayload)));

        _ = await harness.TranscribeAsync(detectLanguage: true);
        _ = await harness.TranscribeAsync(detectLanguage: true);

        var entries = harness.Logger.Entries;
        AssertEx.Equal(expected: 1, entries.Count(static entry => entry.Level == LogLevel.Warning), "One Warning for the whole transcriber.");
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Warning, "Later occurrences are logged at Debug"), "The Warning explains itself.");
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Debug, NullStringError), "The later occurrence still names the daemon's error.");
        AssertEx.Equal(expected: 2, entries.Count(static entry => entry.Level == LogLevel.Debug && entry.Message.StartsWith("Retrying", StringComparison.Ordinal)),
            "One Debug retry line per window.");
    }

    [Test]
    public async Task Transcribe_ARejectionThatIsNotRetried_WarnsWithTheBodyEveryTime()
    {
        await using var harness = new TranscriberHarness(NullStringError, HttpStatusCode.InternalServerError);

        _ = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync(detectLanguage: false));
        _ = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync(detectLanguage: false));

        AssertEx.Equal(expected: 2, harness.Logger.Entries.Count(static entry => entry.Level == LogLevel.Warning
                                                                                 && entry.Message.Contains("rejected a transcription request (500): basic_string", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Transcribe_DetectingALanguage_WhenTheRetryFailsToo_ThrowsTheRejection()
    {
        await using var harness = new TranscriberHarness(Sequence((HttpStatusCode.InternalServerError, NullStringError),
            (HttpStatusCode.InternalServerError, NullStringError)));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync(detectLanguage: true));

        AssertEx.Contains(exception.Message, "rejected the audio", StringComparison.Ordinal);
        AssertEx.Equal(expected: 2, harness.Handler.CallCount, "Retried once, never twice.");
        AssertEx.Equal(expected: 1, harness.Supervisor.LeasesDisposed);
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Warning, "rejected a transcription request (500): basic_string"),
            "The failed retry keeps its own Warning with the body.");
    }

    [Test]
    public async Task Transcribe_WithAnExplicitLanguage_NeverAsksForProbabilitiesAndDoesNotRetry()
    {
        await using var harness = new TranscriberHarness(NullStringError, HttpStatusCode.InternalServerError);

        _ = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync(detectLanguage: true,
            static request => request with
            {
                LanguageMode = WhisperLanguageMode.Explicit,
                LanguageCode = "en"
            }));

        AssertEx.Equal(expected: 1, harness.Handler.CallCount, "Nothing to fall back from: probabilities were never asked for.");
        AssertEx.Equal("true", NoLanguageProbabilities(AssertEx.NotNull(harness.Handler.LastRequestBody)));
    }

    [Test]
    [Arguments(false, HttpStatusCode.InternalServerError)]
    [Arguments(true, HttpStatusCode.BadRequest)]
    public async Task Transcribe_OtherRejections_AreNotRetried(bool detectLanguage, HttpStatusCode status)
    {
        await using var harness = new TranscriberHarness(NullStringError, status);

        _ = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync(detectLanguage));

        AssertEx.Equal(expected: 1, harness.Handler.CallCount);
    }

    [Test]
    public async Task Transcribe_WhenTheRuntimeIsUnreachable_ThrowsTypedAndReleasesTheLease()
    {
        await using var harness = new TranscriberHarness(static _ => throw new HttpRequestException("connection refused"));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync());

        AssertEx.Contains(exception.Message, "could not be reached", StringComparison.Ordinal);
        AssertEx.Equal(expected: 1, harness.Supervisor.LeasesDisposed);
    }

    [Test]
    public async Task Transcribe_WhenTheDaemonDiedMidRequest_SurfacesTheSupervisorsExitVerdict()
    {
        // The 2026-09-22 tester box: whisper-server reported ready, then died on the first /inference and the client saw a
        // connection reset. The supervisor's "process exited" verdict replaces "could not be reached", keeping the reset as cause.
        await using var harness = new TranscriberHarness(static _ => throw new HttpRequestException("An existing connection was forcibly closed by the remote host."));
        harness.Supervisor.RequestFailureVerdict = new WhisperRuntimeException("The transcription runtime process exited (exit code -1073740791).");

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => harness.TranscribeAsync());

        AssertEx.Contains(exception.Message, "exit code -1073740791", StringComparison.Ordinal);
        AssertEx.Equal(expected: (long?)1, harness.Supervisor.ReportedGeneration, "The failure must be reported against the leased endpoint's generation.");
        AssertEx.Equal(expected: 1, harness.Supervisor.LeasesDisposed);
    }

    [Test]
    public async Task Transcribe_RewindsTheAudioAndLeavesTheCallersStreamOpen()
    {
        // The caller owns the stream: the adapter neither disposes it nor requires it positioned, because a retry or
        // a probe upstream may already have read it.
        await using var harness = new TranscriberHarness("""{"text":"x","duration":1.0,"segments":[]}""");
        await using var audio = new MemoryStream(Encoding.ASCII.GetBytes("RIFFxxxxWAVE"));
        audio.Seek(offset: 0, SeekOrigin.End);

        await harness.Transcriber.TranscribeAsync("base",
            new WhisperTranscriptionRequest
            {
                Audio = audio,
                ContentType = "audio/wav"
            },
            CancellationToken.None);

        AssertEx.True(audio.CanRead, "The caller's stream must not be disposed by the adapter.");
        AssertEx.Contains(AssertEx.NotNull(harness.Handler.LastRequestBody), "RIFFxxxxWAVE", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Doubles cross a JSON boundary here, so an exact comparison would assert the serializer's rounding rather
    ///     than the mapping. The tolerance is far tighter than any mapping error could be.
    /// </summary>
    private const string NullStringError = "basic_string: construction from null is not valid";

    private const string SilentPayload = """{"text":"","duration":1.0,"segments":[]}""";

    private static Func<HttpRequestMessage, HttpResponseMessage> Sequence(params (HttpStatusCode Status, string Body)[] responses)
    {
        var next = 0;
        return _ =>
        {
            var (status, body) = responses[Math.Min(next++, responses.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        };
    }

    /// <summary>The value of the multipart field, read off the recorded body.</summary>
    private static string NoLanguageProbabilities(string body)
    {
        var name = body.IndexOf("name=no_language_probabilities", StringComparison.Ordinal);
        AssertEx.True(name >= 0, "The field is always sent.");
        var start = body.IndexOf("\r\n\r\n", name, StringComparison.Ordinal) + 4;
        return body[start..body.IndexOf("\r\n", start, StringComparison.Ordinal)];
    }

    private static void AssertClose(double expected, double? actual, string what)
    {
        AssertEx.True(actual is { } value && Math.Abs(value - expected) < 1e-6,
            $"The {what} was {actual?.ToString(CultureInfo.InvariantCulture) ?? "null"}, expected {expected.ToString(CultureInfo.InvariantCulture)}.");
    }

    private sealed class TranscriberHarness : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;

        public TranscriberHarness(string payload, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(payload, Encoding.UTF8)
                };
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                return response;
            })
        {
        }

        public TranscriberHarness(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            Handler = new RecordingHandler(responder);
            _httpClient = new HttpClient(Handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            Supervisor = new FakeTranscriptionSupervisor();
            Transcriber = new WhisperServerTranscriber(Supervisor, _httpClient, new WhisperRuntimeOptions(), Logger);
        }

        public RecordingHandler Handler { get; }

        public RecordingLogger<WhisperServerTranscriber> Logger { get; } = new();

        public FakeTranscriptionSupervisor Supervisor { get; }

        public WhisperServerTranscriber Transcriber { get; }

        public async Task<WhisperTranscriptionResult> TranscribeAsync(bool detectLanguage = true,
            Func<WhisperTranscriptionRequest, WhisperTranscriptionRequest>? configure = null)
        {
            await using var audio = new MemoryStream(Encoding.ASCII.GetBytes("RIFFxxxxWAVE"));
            var request = new WhisperTranscriptionRequest
            {
                Audio = audio,
                ContentType = "audio/wav",
                DetectLanguage = detectLanguage
            };

            return await Transcriber.TranscribeAsync("base", configure is null ? request : configure(request), CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            Handler.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Records what actually went on the wire, which is the only way to assert the multipart contract.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private int _callCount;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public string? LastRequestBody { get; private set; }

        public List<string> RequestBodies { get; } = [];

        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (LastRequestBody is not null)
            {
                RequestBodies.Add(LastRequestBody);
            }

            return _responder(request);
        }
    }

    /// <summary>
    ///     A supervisor that hands out endpoints and leases on script. The real one needs a child process to say
    ///     anything, and the subject here is the adapter above it, not the daemon lifecycle.
    /// </summary>
    private sealed class FakeTranscriptionSupervisor : IWhisperServerSupervisor
    {
        private int _leasesDisposed;

        /// <summary>How many lease acquisitions refuse before one succeeds — a model switch landing mid-request.</summary>
        public int LeaseRefusals { get; set; }

        public int EnsureCallCount { get; private set; }

        public int LeaseAttemptCount { get; private set; }

        public int LeasesDisposed => Volatile.Read(ref _leasesDisposed);

        public Task<WhisperServerEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct)
        {
            EnsureCallCount++;
            return Task.FromResult(new WhisperServerEndpoint
            {
                ModelId = modelId,
                Generation = EnsureCallCount,
                BaseAddress = new Uri("http://127.0.0.1:18300/")
            });
        }

        public Task<WhisperServerEvictResult> EvictAsync(CancellationToken ct) =>
            Task.FromResult(new WhisperServerEvictResult
            {
                Evicted = true,
                Activity = new WhisperRuntimeActivitySnapshot
                {
                    ActiveTranscriptionCount = 0,
                    SpawnReadinessCount = 0,
                    ResidentProcessCount = 0,
                    MutationReserved = false,
                    EvictionReserved = false
                }
            });

        public IWhisperTranscriptionLease? TryAcquireTranscriptionLease(string modelId, long generation)
        {
            LeaseAttemptCount++;
            if (LeaseAttemptCount <= LeaseRefusals)
            {
                return null;
            }

            return new CountingLease(this);
        }

        /// <summary>What a transport-failure report answers: the supervisor's verdict that the daemon exited, or null.</summary>
        public WhisperRuntimeException? RequestFailureVerdict { get; set; }

        public long? ReportedGeneration { get; private set; }

        public Task<WhisperRuntimeException?> ReportRequestFailureAsync(long generation, Exception cause, CancellationToken ct)
        {
            ReportedGeneration = generation;
            return Task.FromResult(RequestFailureVerdict);
        }

        public WhisperRuntimeStatusSnapshot GetStatus() =>
            new()
            {
                State = WhisperRuntimeState.Ready,
                LoadedModelId = "base",
                Backend = WhisperBackend.Cpu,
                BinaryVersion = "b5130",
                BinarySource = WhisperBinarySource.Pinned,
                SupportsTranscode = false
            };

        private sealed class CountingLease : IWhisperTranscriptionLease
        {
            private readonly FakeTranscriptionSupervisor _owner;

            public CountingLease(FakeTranscriptionSupervisor owner)
            {
                _owner = owner;
            }

            public void Touch()
            {
                // The idle clock is the supervisor's; nothing here needs to observe a touch.
            }

            public void Dispose() =>
                Interlocked.Increment(ref _owner._leasesDisposed);
        }
    }

    /// <summary>A stream that reports itself unseekable, which is the single property under test.</summary>
    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
