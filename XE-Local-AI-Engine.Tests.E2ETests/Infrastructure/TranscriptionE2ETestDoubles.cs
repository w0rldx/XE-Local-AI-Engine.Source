namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using System.Buffers.Binary;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The transcriber the browser E2E host answers with. It proves the <b>pipeline</b> — capture, the worklet's
///     resampling, the base64 hub transport, the segmenter, persistence and the push path — and nothing at all about
///     the model: accuracy evidence comes from the opt-in golden run over the real runtime, not from here.
///     <para>
///         Each submitted window is checked for <em>provenance</em>, not merely for loudness. A plain RMS threshold
///         would still pass if the <c>--use-file-for-fake-audio-capture</c> switch were ever dropped, because Chromium
///         then substitutes its ordinary fake microphone, which is a loud beep: the JFK sentence would keep appearing
///         while proving nothing about the fixture, the resampling or the transport. So the double compares the shape
///         of the received audio against the fixture's own shape, and only a match answers with the transcript.
///     </para>
///     <para>
///         The comparison is a 500 ms RMS envelope, correlated against <c>jfk.wav</c>'s envelope at the best circular
///         lag (the browser plays the file looping, so a window may straddle the loop boundary and the fake is handed
///         no offset to align on — <see cref="WhisperTranscriptionRequest" /> carries none). The envelope is coarse and
///         the threshold loose on purpose: the browser's auto gain, its 48 kHz to 16 kHz resample and the frame
///         boundaries all change sample values, but none of them move where the speech is.
///     </para>
/// </summary>
internal sealed class FakeJfkWhisperTranscriber : IWhisperTranscriber
{
    /// <summary>The transcript a matching window is answered with; the E2E asserts a substring of it.</summary>
    internal const string JfkText = "And so my fellow Americans, ask not what your country can do for you";

    /// <summary>Envelope correlation a window must clear to be accepted as the fixture's audio.</summary>
    internal const double CorrelationThreshold = 0.8;

    /// <summary>
    ///     Blocks a window must span before the fake renders a verdict. The segmenter submits a growing window every
    ///     tick, so the first non-silent one is a second long: two envelope blocks, over which Pearson is always ±1 and
    ///     the browser's fade-in defeats the flat-envelope guard. Four seconds is enough shape to judge and still
    ///     inside the default five-second window; at three the tone control measured 0.76, within 0.04 of the threshold.
    /// </summary>
    internal const int MinimumBlocksForAVerdict = 8;

    private const int BlockMs = 500;
    private const int SamplesPerBlock = (WavSampleRate / 1000) * BlockMs;
    private const int WavSampleRate = 16_000;

    /// <summary>Peak block RMS, on a full-scale-is-one scale, below which a window carries no audio at all.</summary>
    private const double SilenceFloor = 0.005;

    /// <summary>
    ///     Standard deviation over mean, below which an envelope is flat rather than speech-shaped. A tone or a beep
    ///     lands here; correlating a flat line against anything is a coin toss over the candidate lags, and this is
    ///     what keeps that coin toss out of the negative control's verdict.
    /// </summary>
    private const double FlatEnvelopeVariationFloor = 0.15;

    private static readonly Lazy<double[]> FixtureEnvelope = new(LoadFixtureEnvelope, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lock _gate = new();
    private TaskCompletionSource<double> _firstNonSilentInput = NewSignal();
    private double? _lastCorrelation;

    /// <summary>
    ///     Completes the first time this is handed a window that is not silence, carrying the correlation measured for
    ///     it — and it is completed <b>before</b> the accept/reject decision. Awaiting it is how the negative control
    ///     tells "the provenance check ran and rejected this audio" from "no audio ever arrived", which elapsed silence
    ///     could never distinguish.
    /// </summary>
    internal TaskCompletionSource<double> FirstNonSilentInput
    {
        get
        {
            lock (_gate)
            {
                return _firstNonSilentInput;
            }
        }
    }

    /// <summary>The correlation measured for the most recent non-silent window, or null if none has arrived.</summary>
    internal double? LastCorrelation
    {
        get
        {
            lock (_gate)
            {
                return _lastCorrelation;
            }
        }
    }

    /// <summary>
    ///     Re-arms the signal and clears the measurement. The host is shared for the whole test session, so without
    ///     this the second audio test would await a task the first one already completed.
    /// </summary>
    internal void ResetForTests()
    {
        lock (_gate)
        {
            _firstNonSilentInput = NewSignal();
            _lastCorrelation = null;
        }
    }

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wav = await ReadAndRewindAsync(request.Audio, ct);
        var (offset, length) = FindDataChunk(wav);
        var pcm = wav.AsSpan(offset, length);
        var durationSeconds = (length / 2) / (double)WavSampleRate;

        var envelope = RmsEnvelope(pcm);
        if (envelope.Length == 0 || envelope.Max() < SilenceFloor)
        {
            return Rejected(durationSeconds);
        }

        // Too short to judge: neither accepted nor signalled, so the next tick's longer window decides.
        if (envelope.Length < MinimumBlocksForAVerdict)
        {
            return Rejected(durationSeconds);
        }

        var correlation = BestCircularCorrelation(envelope, FixtureEnvelope.Value);
        TaskCompletionSource<double> signal;
        lock (_gate)
        {
            _lastCorrelation = correlation;
            signal = _firstNonSilentInput;
        }

        signal.TrySetResult(correlation);

        return correlation >= CorrelationThreshold
            ? new WhisperTranscriptionResult
            {
                Text = JfkText,
                Segments = [new WhisperTranscriptSegment { StartSeconds = 0, EndSeconds = durationSeconds, Text = JfkText, Confidence = 0.9 }],
                DetectedLanguageCode = "en",
                DetectedLanguageProbability = 0.99,
                DurationSeconds = durationSeconds
            }
            : Rejected(durationSeconds);
    }

    private static TaskCompletionSource<double> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WhisperTranscriptionResult Rejected(double durationSeconds) =>
        new() { Text = string.Empty, Segments = [], DetectedLanguageCode = null, DetectedLanguageProbability = null, DurationSeconds = durationSeconds };

    private static async Task<byte[]> ReadAndRewindAsync(Stream audio, CancellationToken ct)
    {
        // The stream belongs to the segmenter, which reuses it: read it whole, then put the cursor back where it was.
        var start = audio.Position;
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct);
        audio.Position = start;
        return buffer.ToArray();
    }

    private static (int Offset, int Length) FindDataChunk(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The submitted audio is not a RIFF/WAVE stream.");
        }

        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(position + 4, 4));
            var body = position + 8;
            if (wav.Slice(position, 4).SequenceEqual("data"u8))
            {
                return (body, Math.Min(declared, wav.Length - body));
            }

            position = body + declared + (declared & 1);
        }

        throw new InvalidDataException("The submitted WAVE stream has no data chunk.");
    }

    private static double[] RmsEnvelope(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        if (sampleCount == 0)
        {
            return [];
        }

        // A window shorter than one block still gets one block, so a flush of a stub of audio is measured rather
        // than silently read as silence.
        var blocks = Math.Max(1, sampleCount / SamplesPerBlock);
        var envelope = new double[blocks];
        for (var block = 0; block < blocks; block++)
        {
            var from = block * SamplesPerBlock;
            var to = block == blocks - 1 ? Math.Max(sampleCount, from + 1) : from + SamplesPerBlock;
            to = Math.Min(to, sampleCount);

            var sumOfSquares = 0.0;
            for (var sample = from; sample < to; sample++)
            {
                var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sample * 2, 2)) / 32768.0;
                sumOfSquares += value * value;
            }

            envelope[block] = Math.Sqrt(sumOfSquares / Math.Max(1, to - from));
        }

        return envelope;
    }

    private static double BestCircularCorrelation(double[] received, double[] fixtureEnvelope)
    {
        if (received.Length < 2 || fixtureEnvelope.Length < received.Length)
        {
            return 0;
        }

        if (CoefficientOfVariation(received) < FlatEnvelopeVariationFloor)
        {
            return 0;
        }

        var aligned = new double[received.Length];
        var best = 0.0;
        for (var lag = 0; lag < fixtureEnvelope.Length; lag++)
        {
            for (var index = 0; index < received.Length; index++)
            {
                aligned[index] = fixtureEnvelope[(lag + index) % fixtureEnvelope.Length];
            }

            best = Math.Max(best, Pearson(received, aligned));
        }

        return best;
    }

    private static double CoefficientOfVariation(double[] values)
    {
        var mean = values.Average();
        if (mean <= 0)
        {
            return 0;
        }

        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Length;
        return Math.Sqrt(variance) / mean;
    }

    private static double Pearson(double[] left, double[] right)
    {
        var leftMean = left.Average();
        var rightMean = right.Average();

        var covariance = 0.0;
        var leftVariance = 0.0;
        var rightVariance = 0.0;
        for (var index = 0; index < left.Length; index++)
        {
            var leftDelta = left[index] - leftMean;
            var rightDelta = right[index] - rightMean;
            covariance += leftDelta * rightDelta;
            leftVariance += leftDelta * leftDelta;
            rightVariance += rightDelta * rightDelta;
        }

        var denominator = Math.Sqrt(leftVariance * rightVariance);
        return denominator <= 0 ? 0 : covariance / denominator;
    }

    private static double[] LoadFixtureEnvelope()
    {
        var wav = File.ReadAllBytes(FakeAudioFixtures.JfkWavPath);
        var (offset, length) = FindDataChunk(wav);
        return RmsEnvelope(wav.AsSpan(offset, length));
    }
}

/// <summary>
///     The WAV files the browser's fake microphone is pointed at. <see cref="JfkWavPath" /> is the repository fixture,
///     linked into this project's output; <see cref="ToneWavPath" /> is generated on first use so the negative control
///     needs no second binary in the repository.
/// </summary>
internal static class FakeAudioFixtures
{
    private const int DurationSeconds = 11;
    private const double ToneAmplitude = 0.3;
    private const double ToneFrequencyHz = 440;
    private const int SampleRate = 16_000;

    private static readonly Lazy<string> GeneratedTone = new(WriteToneWav, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The linked <c>jfk.wav</c>: 16 kHz mono 16-bit, about eleven seconds of speech.</summary>
    internal static string JfkWavPath { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jfk.wav");

    /// <summary>
    ///     A 440 Hz tone of the same length and format. It is loud, so it is never mistaken for silence, and its
    ///     envelope is flat, so it cannot be mistaken for the fixture's speech.
    /// </summary>
    internal static string ToneWavPath => GeneratedTone.Value;

    private static string WriteToneWav()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440hz.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sampleCount = SampleRate * DurationSeconds;
        var pcm = new byte[sampleCount * 2];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var value = ToneAmplitude * Math.Sin(2 * Math.PI * ToneFrequencyHz * sample / SampleRate);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 2, 2), (short)Math.Round(value * short.MaxValue));
        }

        File.WriteAllBytes(path, WavPcm16.Wrap(pcm, SampleRate));
        return path;
    }
}
