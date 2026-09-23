namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Re-records <c>jfk-golden-base.json</c> against a real <c>whisper-server</c>. Opt-in; nothing invokes it.
/// </summary>
/// <remarks>
///     <para>
///         Two passes over the same server in one run. The single-shot pass is one request over the whole clip — the
///         same call the batch path makes — and it is what the golden assertion compares committed text against; the
///         chunked pass drives the <b>real</b> segmenter and records every window it submitted. Recording the
///         single-shot text rather than writing a literal into the test is what stops the assertion rotting into
///         "the committed text is non-empty" the first time the pinned model or VAD file moves.
///     </para>
///     <para>
///         It goes through the provider's own <see cref="WhisperServerTranscriber" />, never its own HTTP. No
///         whisper-server route, flag or JSON shape may exist outside the provider project, and a recorder that
///         spoke the wire protocol itself would also stop proving that the shipped adapter works.
///     </para>
/// </remarks>
[Category(TestCategories.ExternalInfra)]
public sealed class WhisperGoldenFixtureRecorder
{
    private const string ServerVariable = "XE_WHISPER_GOLDEN_SERVER_URL";

    /// <summary>The model the recording runs under, recorded into the fixture beside its digest.</summary>
    private const string ModelFileName = "ggml-base.bin";

    private const string ModelId = "base";
    private const int MaxWindowSeconds = 5;
    private const int TailGuardMs = 800;
    private const int TickMs = 1_000;
    private const int PushMs = 2_000;

    [Test]
    public async Task RecordJfkGoldenFixture()
    {
        var baseUrl = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Skip.Test($"Set {ServerVariable} to a running whisper-server base URL to re-record the golden fixture "
                      + "(start it on the pinned VAD file, see the slice plan's re-record recipe).");
            return;
        }

        var clipPath = RecordedWhisperTranscriber.FixturePath("jfk.wav");
        var clip = await File.ReadAllBytesAsync(clipPath);
        var pcm = WavPayload.Read(clip);

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var transcriber = new WhisperServerTranscriber(new GoldenRecorderSupervisor(new Uri(baseUrl, UriKind.Absolute)),
            httpClient,
            new WhisperRuntimeOptions());

        var singleShot = await TranscribeWholeClipAsync(transcriber, clip);
        var windows = await RecordChunkedPassAsync(transcriber);

        var fixture = new GoldenFixture
        {
            Note = "Re-record when the model, the VAD file, the launch flags or the segmenter's window rules change.",
            Clip = "jfk.wav",
            ClipSha256 = Convert.ToHexStringLower(SHA256.HashData(clip)),
            SampleRate = WavPcm16.SampleRate,
            Model = ModelFileName,
            ModelSha256 = WhisperModelCatalog.Models.Single(model => model.Id == ModelId).Sha256,
            VadModel = "ggml-silero-v6.2.0.bin",
            VadSha256 = WhisperModelCatalog.VadSha256,
            MaxWindowSeconds = MaxWindowSeconds,
            TailGuardMs = TailGuardMs,
            TickMs = TickMs,
            PushMs = PushMs,
            SingleShotText = singleShot,
            Windows = windows
        };

        var destination = SourceTreeFixturePath("jfk-golden-base.json");
        RecordedWhisperTranscriber.WriteFixture(destination, fixture);

        // A recorder that only wrote a file would be a vacuous test: read back what it wrote and assert the two
        // things the golden test cannot run without.
        var written = RecordedWhisperTranscriber.ReadFixture(destination);
        AssertEx.NotEmpty(written.SingleShotText, $"The single-shot pass produced no transcript; the server at {baseUrl} answered nothing.");
        AssertEx.NotEmpty(written.Windows, "The chunked pass submitted no window.");
        AssertEx.Equal(WhisperModelCatalog.VadSha256, written.VadSha256, "The fixture records the pinned VAD digest.");
        AssertEx.Equal(pcm.Length / WavPcm16.BytesPerMillisecond,
            written.Windows[^1].EndMs,
            "The recorded windows must reach the end of the clip.");
    }

    private static async Task<string> TranscribeWholeClipAsync(IWhisperTranscriber transcriber, byte[] clip)
    {
        using var audio = new MemoryStream(clip, writable: false);
        var result = await transcriber.TranscribeAsync(ModelId, new WhisperTranscriptionRequest
        {
            Audio = audio,
            ContentType = "audio/wav",
            LanguageMode = WhisperLanguageMode.Auto,
            Translate = false,
            UseVoiceActivityDetection = true,
            DetectLanguage = true
        }, CancellationToken.None);

        return result.Text.Trim();
    }

    private static async Task<IReadOnlyList<GoldenWindow>> RecordChunkedPassAsync(IWhisperTranscriber transcriber)
    {
        var clip = await File.ReadAllBytesAsync(RecordedWhisperTranscriber.FixturePath("jfk.wav"));
        var pcm = WavPayload.Read(clip);

        var recorder = new RecordingWhisperTranscriber(transcriber);
        var segmenter = new LiveTranscriptionSegmenter(recorder,
            TranscriptChannel.Mono,
            ModelId,
            languageCode: null,
            translate: false,
            new LiveSegmenterSettings
            {
                MaxWindowSeconds = MaxWindowSeconds,
                TailGuardMs = TailGuardMs,
                TickMs = TickMs
            });
        recorder.WindowStartMs = () => segmenter.CommittedEndMs;

        var frameBytes = PushMs * WavPcm16.BytesPerMillisecond;
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            _ = await segmenter.PushAsync(pcm.Slice(offset, Math.Min(frameBytes, pcm.Length - offset)), CancellationToken.None);
        }

        _ = await segmenter.FlushAsync(CancellationToken.None);
        return recorder.Windows;
    }

    /// <summary>Resolves the fixture inside the source tree, so the run produces a committable file.</summary>
    private static string SourceTreeFixturePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = AssertEx.NotNull(directory, "The recorder writes into the source tree, and the repository root was not found above the test binary.");
        return Path.Combine(root.FullName, "XE-Local-AI-Engine.Tests", "Fixtures", "Transcription", fileName);
    }
}

/// <summary>Records what the segmenter submitted and what the real runtime answered, and passes both through.</summary>
/// <remarks>
///     The absolute start of a window is read from the segmenter rather than searched for in the clip: the lane's
///     retained audio always begins at its committed watermark, so the watermark at the moment of the call is the
///     window's start. Searching for the payload inside the clip would mis-locate a window of near-silence.
/// </remarks>
internal sealed class RecordingWhisperTranscriber : IWhisperTranscriber
{
    private readonly List<GoldenWindow> _windows = [];
    private readonly IWhisperTranscriber _inner;

    public RecordingWhisperTranscriber(IWhisperTranscriber inner)
    {
        _inner = inner;
    }

    /// <summary>Reads the lane's watermark, which is where its retained audio starts.</summary>
    public Func<long> WindowStartMs { get; set; } = () => 0;

    public IReadOnlyList<GoldenWindow> Windows => _windows;

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var buffer = new MemoryStream();
        await request.Audio.CopyToAsync(buffer, ct);
        request.Audio.Seek(offset: 0, SeekOrigin.Begin);

        var payload = WavPayload.Read(buffer.ToArray());
        var startMs = WindowStartMs();
        var endMs = startMs + (payload.Length / WavPcm16.BytesPerMillisecond);

        var result = await _inner.TranscribeAsync(modelId, request, ct);

        _windows.Add(new GoldenWindow
        {
            StartMs = startMs,
            EndMs = endMs,
            PcmSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.Span)),
            DetectedLanguageCode = result.DetectedLanguageCode,
            Segments = result.Segments
                             .Select(segment => new GoldenSegment
                             {
                                 StartMs = (long)Math.Round(segment.StartSeconds * 1000, MidpointRounding.AwayFromZero) + startMs,
                                 EndMs = (long)Math.Round(segment.EndSeconds * 1000, MidpointRounding.AwayFromZero) + startMs,
                                 Text = segment.Text,
                                 Confidence = segment.Confidence
                             })
                             .ToList()
        });

        return result;
    }
}

/// <summary>Points the provider's adapter at an already-running server and grants it a lease that does nothing.</summary>
/// <remarks>
///     The recorder starts and stops <c>whisper-server</c> itself, by PID, so there is no process for a supervisor to
///     own. Only the two things the adapter asks for are needed: where the daemon is, and permission to talk to it.
/// </remarks>
internal sealed class GoldenRecorderSupervisor : IWhisperServerSupervisor
{
    private readonly Uri _baseAddress;

    public GoldenRecorderSupervisor(Uri baseAddress)
    {
        _baseAddress = baseAddress;
    }

    public Task<WhisperServerEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct) =>
        Task.FromResult(new WhisperServerEndpoint { ModelId = modelId, Generation = 1, BaseAddress = _baseAddress });

    public Task<WhisperServerEvictResult> EvictAsync(CancellationToken ct) =>
        throw new NotSupportedException("The recorder does not own the daemon's lifetime.");

    public IWhisperTranscriptionLease? TryAcquireTranscriptionLease(string modelId, long generation) =>
        new NoOpLease();

    public Task<WhisperRuntimeException?> ReportRequestFailureAsync(long generation, Exception cause, CancellationToken ct) =>
        Task.FromResult<WhisperRuntimeException?>(null);

    public WhisperRuntimeStatusSnapshot GetStatus() =>
        new() { State = WhisperRuntimeState.Ready, LoadedModelId = null, Backend = null, BinaryVersion = null, BinarySource = null, SupportsTranscode = false };

    private sealed class NoOpLease : IWhisperTranscriptionLease
    {
        public void Touch()
        {
        }

        public void Dispose()
        {
        }
    }
}
