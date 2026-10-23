namespace XE_Local_AI_Engine.Tests.Transcription;

using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins where a file upload is cut before transcription, on synthetic tone bursts parted by digital silence.
/// </summary>
/// <remarks>
///     The two long clips mirror the tester's N-72 recordings: seventeen short utterances 34 s apart and four utterances
///     30 s apart, which whisper-server returned as one and two segments when sent whole.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SilenceSplitterTests
{
    [Test]
    public async Task Split_SeventeenUtterancesPartedBy34Seconds_YieldsSeventeenRegions()
    {
        // 1.28 s of speech every 35.28 s, the shape of the tester's ten-minute clip.
        var starts = Enumerable.Range(0, 17).Select(index => 450L + (index * 35_280L)).ToList();
        const long TotalMs = 17 * 35_280L;

        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, TotalMs, starts, 1_280));

        AssertEx.Equal(17, regions.Count, "Every utterance must reach the runtime in its own request.");
        AssertTiles(regions, TotalMs);
        for (var index = 0; index < starts.Count; index++)
        {
            AssertEx.True(regions[index].StartMs <= starts[index] && starts[index] + 1_280 <= regions[index].EndMs,
                $"Utterance {index} must lie whole inside region {index}.");
        }
    }

    [Test]
    public async Task Split_FourUtterancesPartedBy30Seconds_YieldsFourRegions()
    {
        long[] starts = [480, 32_480, 64_480, 96_480];
        const long TotalMs = 100_000;

        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, TotalMs, starts, 2_000));

        AssertEx.Equal(4, regions.Count);
        AssertTiles(regions, TotalMs);
        AssertEx.Equal(new SilenceSplitter.Region(0, 17_480), regions[0], "The cut sits in the middle of the gap, 2480 + 30000 / 2 ms.");
    }

    [Test]
    public async Task Split_ContinuousSpeech_IsOneRegionSpanningTheClip()
    {
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, 8_000, [0], 8_000));

        AssertEx.Equal(1, regions.Count);
        AssertEx.Equal(new SilenceSplitter.Region(0, 8_000), regions[0]);
    }

    [Test]
    public async Task Split_DigitalSilenceOnly_IsOneRegionSpanningTheClip()
    {
        // Nothing to cut at: the runtime still gets the whole file, and its own VAD answers "no speech".
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, 5_000, [], 0));

        AssertEx.Equal(1, regions.Count);
        AssertEx.Equal(new SilenceSplitter.Region(0, 5_000), regions[0]);
    }

    [Test]
    public async Task Split_ShortDropoutInsideSpeech_IsNotACut()
    {
        // A 50 ms dropout is a pause between words, far below the 1.5 s a gap needs.
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, 5_000, [0, 2_050], 2_000));

        AssertEx.Equal(1, regions.Count, "A dropout shorter than the minimum gap must not split a word run.");
    }

    [Test]
    public async Task Split_GapJustUnderTheMinimum_IsNotACut()
    {
        // The control for the two cases above and below: 1.4 s of silence is still one run.
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(16_000, 1, 6_000, [0, 2_400], 1_000));

        AssertEx.Equal(1, regions.Count, $"A gap shorter than {SilenceSplitter.MinGapMs} ms must not cut.");
    }

    [Test]
    public async Task Split_IsolatedClick_JoinsItsNeighbourInsteadOfStandingAlone()
    {
        // A 100 ms click between two utterances is shorter than the minimum speech run, so it never becomes a region.
        var wav = TranscriptionAudioFixtures.ToneWav(16_000, 1, 22_000, [0, 20_000], 1_000);
        var click = TranscriptionAudioFixtures.ToneWav(16_000, 1, 100, [0], 100);
        click.AsSpan(WavPcm16.HeaderLength).CopyTo(wav.AsSpan(WavPcm16.HeaderLength + (10_000 * WavPcm16.BytesPerMillisecond)));

        var regions = await SplitAsync(wav);

        AssertEx.Equal(2, regions.Count, "Two utterances and a click make two regions, not three.");
        AssertTiles(regions, 22_000);
    }

    [Test]
    public async Task Split_Stereo44k_CutsInMillisecondsFromTheHeaderRate()
    {
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(44_100, 2, 12_000, [0, 8_000], 1_000));

        AssertEx.Equal(2, regions.Count);
        AssertTiles(regions, 12_000);
        AssertEx.Equal(4_500L, regions[0].EndMs, "The cut sits halfway between 1000 ms and 8000 ms whatever the rate.");
    }

    [Test]
    public async Task Split_192kEightChannels_CutsAtTheUpperBound()
    {
        // The largest layout the header reader admits; one analysis frame is 3840 samples x 16 bytes, about 60 KB.
        var regions = await SplitAsync(TranscriptionAudioFixtures.ToneWav(192_000, 8, 3_000, [0, 2_500], 500));

        AssertEx.Equal(2, regions.Count);
        AssertTiles(regions, 3_000);
        AssertEx.Equal(1_500L, regions[0].EndMs, "The cut sits halfway between 500 ms and 2500 ms.");
    }

    private static async Task<IReadOnlyList<SilenceSplitter.Region>> SplitAsync(byte[] wav)
    {
        await using var stream = new MemoryStream(wav, writable: false);
        var layout = WavPcm16.TryReadLayout(stream);
        AssertEx.True(layout.HasValue, "The synthetic clip must be a readable 16-bit PCM WAV.");
        return await SilenceSplitter.SplitAsync(stream, layout.GetValueOrDefault(), CancellationToken.None);
    }

    // Regions must cover the clip edge to edge with no gap and no overlap: nothing below the threshold is dropped.
    private static void AssertTiles(IReadOnlyList<SilenceSplitter.Region> regions, long totalMs)
    {
        AssertEx.Equal(0L, regions[0].StartMs, "The first region starts at the clip's first sample.");
        AssertEx.Equal(totalMs, regions[^1].EndMs, "The last region ends at the clip's last sample.");
        for (var index = 1; index < regions.Count; index++)
        {
            AssertEx.Equal(regions[index - 1].EndMs, regions[index].StartMs, $"Region {index} must start where region {index - 1} ends.");
        }
    }
}
