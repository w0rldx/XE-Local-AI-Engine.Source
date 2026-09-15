namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Buffers.Binary;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Calibrates the E2E double's provenance check without a browser, which is what makes the browser test's verdict
///     readable: it fixes what the correlation is for audio that IS the fixture, for audio that is merely loud, and
///     for silence. A browser adds auto gain, a 48 kHz to 16 kHz resample and frame boundaries on top of this, so the
///     live numbers are lower — the live run logs its own and the slice report quotes both.
/// </summary>
public sealed class FakeJfkWhisperTranscriberTests
{
    private const int WindowSeconds = 5;
    private const int SampleRate = 16_000;

    /// <summary>The modulated control's burst rhythm: 900 ms of tone, 600 ms of nothing, repeating.</summary>
    /// <remarks>
    ///     Chosen so no 500 ms envelope block lands wholly inside one burst and the 1.5 s period is neither the
    ///     fixture's speech rhythm nor a multiple of the block, which is what keeps the envelope varied and
    ///     unrelated at the same time.
    /// </remarks>
    private const int BurstOffMs = 600;

    private const int BurstOnMs = 900;

    /// <summary>The fixture's own length, so the control's envelope is as long as the one it is correlated against.</summary>
    private const int ModulatedSeconds = 11;

    [Test]
    public async Task AWindowCutFromTheFixture_AcrossTheLoopBoundary_IsAcceptedAndTranscribed()
    {
        var transcriber = new FakeJfkWhisperTranscriber();

        // Starts seven seconds into an eleven-second file and runs five seconds, so it wraps — exactly what the
        // browser's looping fake device produces, and the case a non-circular alignment would reject.
        var result = await TranscribeAsync(transcriber, LoopSlice(FixturePcm(), 7 * SampleRate, WindowSeconds * SampleRate));

        await Console.Out.WriteLineAsync($"[DIAG] fixture-slice correlation={transcriber.LastCorrelation}");
        await Assert.That(transcriber.LastCorrelation).IsNotNull();
        await Assert.That(transcriber.LastCorrelation!.Value).IsGreaterThanOrEqualTo(FakeJfkWhisperTranscriber.CorrelationThreshold);
        await Assert.That(result.Segments.Count).IsEqualTo(1);
        await Assert.That(result.Segments[0].Text).IsEqualTo(FakeJfkWhisperTranscriber.JfkText);
        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsTrue();
    }

    [Test]
    public async Task ALoudUnrelatedTone_IsMeasuredAndRejected()
    {
        var transcriber = new FakeJfkWhisperTranscriber();

        var result = await TranscribeAsync(transcriber, TonePcm(WindowSeconds * SampleRate));

        // Measured, then rejected: the signal completing is what tells a rejection from audio that never arrived.
        await Console.Out.WriteLineAsync($"[DIAG] tone correlation={transcriber.LastCorrelation}");
        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsTrue();
        await Assert.That(transcriber.LastCorrelation!.Value).IsLessThan(FakeJfkWhisperTranscriber.CorrelationThreshold);
        await Assert.That(result.Segments.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     The case the flat-envelope guard hides. A continuous tone never reaches the Pearson search at all — it is
    ///     answered 0 by the <c>CoefficientOfVariation</c> short-circuit — so the existing controls prove the guard
    ///     fires and nothing about whether the correlation discriminates. This one is loud, varied and not the
    ///     fixture: the search runs, and the number it returns is the evidence.
    /// </summary>
    [Test]
    public async Task AModulatedUnrelatedTone_IsMeasuredByTheCorrelationSearchAndRejected()
    {
        var transcriber = new FakeJfkWhisperTranscriber();

        var result = await TranscribeAsync(transcriber, TonePcm(ModulatedSeconds * SampleRate, BurstOnMs, BurstOffMs));
        var correlation = transcriber.LastCorrelation;

        await Console.Out.WriteLineAsync($"[DIAG] modulated-tone correlation={correlation}");
        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsTrue();

        // Above zero is the whole point: zero is what the flat-envelope short-circuit returns, so a zero here would
        // mean the max-over-lags search never ran and this control proved no more than the continuous tone does.
        await Assert.That(correlation!.Value)
                    .IsGreaterThan(0d)
                    .Because("a zero correlation is the flat-envelope short-circuit, not a measurement.");
        await Assert.That(correlation.Value)
                    .IsLessThan(FakeJfkWhisperTranscriber.CorrelationThreshold)
                    .Because("audio that is loud and varied but is not the fixture must still be rejected.");
        await Assert.That(result.Segments.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     The first non-silent window the segmenter submits is one second long. Two envelope blocks correlate at ±1
    ///     with anything, and the browser's fade-in makes even a flat tone look shaped over two blocks — the fake must
    ///     hold its verdict and its signal until a window carries enough envelope to judge.
    /// </summary>
    [Test]
    public async Task AWindowShorterThanTheVerdictMinimum_IsNeitherAcceptedNorSignalled()
    {
        var transcriber = new FakeJfkWhisperTranscriber();

        var result = await TranscribeAsync(transcriber, LoopSlice(FixturePcm(), 0, SampleRate));

        await Assert.That(result.Segments.Count).IsEqualTo(0);
        await Assert.That(transcriber.LastCorrelation).IsNull();
        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Silence_IsNeverReportedAsInput()
    {
        var transcriber = new FakeJfkWhisperTranscriber();

        var result = await TranscribeAsync(transcriber, new byte[WindowSeconds * SampleRate * 2]);

        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsFalse();
        await Assert.That(transcriber.LastCorrelation).IsNull();
        await Assert.That(result.Segments.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResetForTests_ReArmsTheSignalForTheNextSuite()
    {
        var transcriber = new FakeJfkWhisperTranscriber();
        _ = await TranscribeAsync(transcriber, TonePcm(WindowSeconds * SampleRate));
        var beforeReset = transcriber.FirstNonSilentInput;

        transcriber.ResetForTests();

        await Assert.That(transcriber.FirstNonSilentInput).IsNotSameReferenceAs(beforeReset);
        await Assert.That(transcriber.FirstNonSilentInput.Task.IsCompleted).IsFalse();
        await Assert.That(transcriber.LastCorrelation).IsNull();
    }

    private static async Task<WhisperTranscriptionResult> TranscribeAsync(FakeJfkWhisperTranscriber transcriber, byte[] pcm)
    {
        using var audio = new MemoryStream(WavPcm16.Wrap(pcm), writable: false);
        var result = await transcriber.TranscribeAsync("whisper-base", new WhisperTranscriptionRequest
        {
            Audio = audio,
            ContentType = "audio/wav"
        }, CancellationToken.None);

        // The provider must not consume the caller's stream.
        await Assert.That(audio.Position).IsEqualTo(0L);
        return result;
    }

    private static byte[] FixturePcm()
    {
        var wav = File.ReadAllBytes(FakeAudioFixtures.JfkWavPath);
        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4, 4));
            if (wav.AsSpan(position, 4).SequenceEqual("data"u8))
            {
                return wav.AsSpan(position + 8, Math.Min(declared, wav.Length - position - 8)).ToArray();
            }

            position += 8 + declared + (declared & 1);
        }

        throw new InvalidDataException($"The linked fixture has no data chunk: {FakeAudioFixtures.JfkWavPath}");
    }

    private static byte[] LoopSlice(byte[] pcm, int startSample, int sampleCount)
    {
        var totalSamples = pcm.Length / 2;
        var slice = new byte[sampleCount * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var source = ((startSample + index) % totalSamples) * 2;
            slice[index * 2] = pcm[source];
            slice[(index * 2) + 1] = pcm[source + 1];
        }

        return slice;
    }

    /// <summary>
    ///     A 440 Hz tone, optionally gated into bursts. With <paramref name="burstOnMs" /> zero it is the continuous
    ///     tone <see cref="FakeAudioFixtures.ToneWavPath" /> writes — flat envelope, and rejected without a search.
    /// </summary>
    private static byte[] TonePcm(int sampleCount, int burstOnMs = 0, int burstOffMs = 0)
    {
        var pcm = new byte[sampleCount * 2];
        var periodMs = burstOnMs + burstOffMs;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var gated = periodMs > 0 && (sample * 1_000L / SampleRate) % periodMs >= burstOnMs;
            var value = gated ? 0 : 0.3 * Math.Sin(2 * Math.PI * 440 * sample / SampleRate);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 2, 2), (short)Math.Round(value * short.MaxValue));
        }

        return pcm;
    }
}
