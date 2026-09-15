namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>One recorded inference window: what the segmenter submitted and what the real runtime answered.</summary>
internal sealed record GoldenWindow
{
    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    /// <summary>SHA-256 of the raw PCM payload, so a replay against different audio fails instead of passing.</summary>
    public required string PcmSha256 { get; init; }

    public string? DetectedLanguageCode { get; init; }

    public required IReadOnlyList<GoldenSegment> Segments { get; init; }
}

/// <summary>One recorded segment. Milliseconds for readability; the fake converts back to the provider's seconds.</summary>
internal sealed record GoldenSegment
{
    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required string Text { get; init; }

    public double? Confidence { get; init; }
}

/// <summary>The recorded whole-clip run plus every window of the chunked run, and the runtime they came from.</summary>
internal sealed record GoldenFixture
{
    public required string Note { get; init; }

    public required string Clip { get; init; }

    public required string ClipSha256 { get; init; }

    public required int SampleRate { get; init; }

    public required string Model { get; init; }

    public required string ModelSha256 { get; init; }

    public required string VadModel { get; init; }

    /// <summary>Digest of the VAD weights the recording ran under; the golden test pins it to the catalogue's.</summary>
    public required string VadSha256 { get; init; }

    public required int MaxWindowSeconds { get; init; }

    public required int TailGuardMs { get; init; }

    public required int TickMs { get; init; }

    public required int PushMs { get; init; }

    /// <summary>One whole-clip transcription, which is what the golden assertion compares against.</summary>
    public required string SingleShotText { get; init; }

    public required IReadOnlyList<GoldenWindow> Windows { get; init; }
}

/// <summary>
///     Replays a recorded run of the real runtime, window by window, as an <see cref="IWhisperTranscriber" />.
/// </summary>
/// <remarks>
///     <para>
///         A hand-written fake, which is the last rung of the repository's mock order, and the reason is that the
///         three rungs above it cannot express this. The real thing is a subprocess, there is no repository fake seam
///         for whisper, and a substitute would need a call handler that answers <em>this</em> window with <em>that</em>
///         recorded response and fails loudly on an unknown one — which is this class, written inside a lambda.
///     </para>
///     <para>
///         It never invents a response. An unmatched window throws, naming both ranges, because a fake that returned
///         an empty result on a miss would let a changed segmenter pass a golden test in silence.
///     </para>
/// </remarks>
internal sealed class RecordedWhisperTranscriber : IWhisperTranscriber
{
    private static readonly JsonSerializerOptions FixtureSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly GoldenFixture _fixture;
    private readonly List<(long StartMs, long EndMs)> _submissions = [];
    private int _next;

    private RecordedWhisperTranscriber(GoldenFixture fixture) =>
        _fixture = fixture;

    /// <summary>The absolute window ranges the segmenter asked for, in order.</summary>
    public IReadOnlyList<(long StartMs, long EndMs)> Submissions => _submissions;

    /// <summary>The recorded whole-clip transcript the golden assertion compares committed text against.</summary>
    public string SingleShotText => _fixture.SingleShotText;

    public GoldenFixture Fixture => _fixture;

    /// <summary>Loads a fixture that was copied beside the test binary.</summary>
    public static RecordedWhisperTranscriber FromFixture(string fixtureFileName) =>
        new(ReadFixture(FixturePath(fixtureFileName)));

    /// <summary>Resolves a fixture file beside the test binary.</summary>
    public static string FixturePath(string fixtureFileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcription", fixtureFileName);

    public static GoldenFixture ReadFixture(string path) =>
        JsonSerializer.Deserialize<GoldenFixture>(File.ReadAllText(path, Encoding.UTF8), FixtureSerializerOptions)
        ?? throw new InvalidOperationException($"The golden fixture at '{path}' is empty.");

    /// <summary>Writes a fixture without a byte-order mark, which is what the repository's other JSON carries.</summary>
    public static void WriteFixture(string path, GoldenFixture fixture) =>
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, FixtureSerializerOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The contract guarantees a seekable stream; asserting it here is what catches a future caller that hands
        // over a network stream, which would fail far away inside the provider's multipart body instead.
        AssertEx.True(request.Audio.CanSeek, "The transcriber is handed a seekable stream so the request can declare its length.");
        AssertEx.Equal("audio/wav", request.ContentType, "The live path submits WAV.");

        var wav = await ReadAndRewindAsync(request.Audio, ct).ConfigureAwait(false);
        var payload = WavPayload.Read(wav);
        var durationMs = payload.Length / 32;

        if (_next >= _fixture.Windows.Count)
        {
            throw new InvalidOperationException(
                $"The segmenter submitted a {durationMs} ms window after the fixture's {_fixture.Windows.Count} recorded windows were exhausted. Re-record the fixture.");
        }

        var window = _fixture.Windows[_next++];
        var expectedMs = window.EndMs - window.StartMs;
        if (expectedMs != durationMs)
        {
            throw new InvalidOperationException(
                $"The segmenter submitted a {durationMs} ms window where the fixture's next recorded window is [{window.StartMs}, {window.EndMs}) ({expectedMs} ms). Re-record the fixture.");
        }

        var actualSha = Convert.ToHexStringLower(SHA256.HashData(payload.Span));
        if (!string.Equals(actualSha, window.PcmSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The audio submitted for [{window.StartMs}, {window.EndMs}) hashed to {actualSha}; the fixture recorded {window.PcmSha256}.");
        }

        _submissions.Add((window.StartMs, window.EndMs));

        // Back to the provider's contract — fractional seconds, relative to the submitted audio — so that the
        // seconds-to-milliseconds conversion in the segmenter is exercised rather than bypassed.
        var segments = window.Segments
            .Select(segment => new WhisperTranscriptSegment((segment.StartMs - window.StartMs) / 1000.0,
                (segment.EndMs - window.StartMs) / 1000.0,
                segment.Text,
                segment.Confidence))
            .ToList();

        return new WhisperTranscriptionResult(string.Join(' ', segments.Select(segment => segment.Text)).Trim(),
            segments,
            window.DetectedLanguageCode,
            window.DetectedLanguageCode is null ? null : 0.99,
            durationMs / 1000.0);
    }

    /// <summary>Buffers the caller's stream so it can be hashed, then rewinds it: the caller still owns it.</summary>
    private static async Task<byte[]> ReadAndRewindAsync(Stream audio, CancellationToken ct)
    {
        var start = audio.Position;
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct).ConfigureAwait(false);
        audio.Seek(start, SeekOrigin.Begin);
        return buffer.ToArray();
    }
}

/// <summary>Finds the <c>data</c> chunk of a RIFF/WAVE file.</summary>
/// <remarks>
///     A chunk walk rather than a fixed 44-byte skip: <c>jfk.wav</c> carries a <c>LIST</c> chunk between the format
///     and the data chunk, so its payload starts at byte 78. A reader that assumed 44 would hash metadata as audio.
/// </remarks>
internal static class WavPayload
{
    public static ReadOnlyMemory<byte> Read(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);

        if (wav.Length < 12
            || !"RIFF"u8.SequenceEqual(wav.AsSpan(0, 4))
            || !"WAVE"u8.SequenceEqual(wav.AsSpan(8, 4)))
        {
            throw new InvalidOperationException("The submitted audio is not a RIFF/WAVE file.");
        }

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4));
            if ("data"u8.SequenceEqual(wav.AsSpan(offset, 4)))
            {
                return wav.AsMemory(offset + 8, Math.Min(size, wav.Length - offset - 8));
            }

            offset += 8 + size + (size % 2);
        }

        throw new InvalidOperationException("The submitted WAV file has no data chunk.");
    }
}
