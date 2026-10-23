namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

/// <summary>
///     Cuts a 16-bit PCM WAV into contiguous regions at long silences, so each utterance reaches the transcription
///     runtime as its own request.
/// </summary>
/// <remarks>
///     whisper-server joins every stretch its voice-activity detector keeps into one decode buffer, so one request over a
///     clip of repeated phrases parted by long pauses comes back as one or two segments; no request field changes that.
///     Regions tile the clip with no hole: a cut sits in the middle of a gap, so audio below the threshold is still sent,
///     never dropped. Accepted ceiling: a fixed level threshold, so a recording whose background noise is louder than it
///     is never cut and keeps the single-request behaviour.
/// </remarks>
public static class SilenceSplitter
{
    /// <summary>The analysis frame length.</summary>
    public const int FrameMs = 20;

    /// <summary>A frame whose channel-averaged RMS is below this fraction of full scale is silent (about -45 dBFS).</summary>
    public const double SilenceRms = 0.005_6;

    /// <summary>A silent run must last at least this long to separate two regions.</summary>
    public const int MinGapMs = 1_500;

    /// <summary>A speech run shorter than this (a click, a breath) is joined to its neighbour rather than cut out alone.</summary>
    public const int MinSpeechMs = 300;

    private const int MinGapFrames = MinGapMs / FrameMs;
    private const int MinSpeechFrames = MinSpeechMs / FrameMs;
    private const double FullScale = 32_768.0;

    /// <summary>
    ///     Measures the PCM payload <paramref name="layout" /> describes and returns the regions to transcribe, in
    ///     milliseconds from the start of the payload; one region spanning the clip when no long silence parts speech.
    /// </summary>
    public static async Task<IReadOnlyList<Region>> SplitAsync(Stream stream, WavPcm16.Layout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var samplesPerFrame = checked(Math.Max(1, (int)((long)layout.SamplesPerSecond * FrameMs / 1000)));
        var frameBytes = checked(samplesPerFrame * layout.BlockAlign);
        var levels = new List<double>((int)Math.Min(int.MaxValue, (layout.DataLength / frameBytes) + 1));
        var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
        try
        {
            stream.Position = layout.DataOffset;
            var remaining = layout.DataLength;
            while (remaining > 0)
            {
                var want = (int)Math.Min(frameBytes, remaining);
                var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, cancellationToken);
                read -= read % layout.BlockAlign;
                if (read == 0)
                {
                    break;
                }

                levels.Add(FrameRms(buffer.AsSpan(0, read), layout.ChannelCount));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var totalMs = checked(layout.DataLength / layout.BlockAlign * 1000 / layout.SamplesPerSecond);
        var cuts = FindCutFrames(CollectionsMarshal.AsSpan(levels));
        var regions = new List<Region>(cuts.Count + 1);
        long start = 0;
        foreach (var cut in cuts)
        {
            var cutMs = checked((long)cut * samplesPerFrame * 1000 / layout.SamplesPerSecond);
            regions.Add(new Region(start, cutMs));
            start = cutMs;
        }

        regions.Add(new Region(start, totalMs));
        return regions;
    }

    /// <summary>Returns the frame indices to cut at: the middle of every long silence between two kept speech runs.</summary>
    public static IReadOnlyList<int> FindCutFrames(ReadOnlySpan<double> frameRms)
    {
        var runs = new List<SpeechRun>();
        var start = -1;
        var lastSpeech = -1;
        for (var i = 0; i < frameRms.Length; i++)
        {
            if (frameRms[i] < SilenceRms)
            {
                continue;
            }

            if (start < 0)
            {
                start = i;
            }
            else if (i - lastSpeech - 1 >= MinGapFrames)
            {
                runs.Add(new SpeechRun(start, lastSpeech + 1));
                start = i;
            }

            lastSpeech = i;
        }

        if (start >= 0)
        {
            runs.Add(new SpeechRun(start, lastSpeech + 1));
        }

        // A short run joins the run before it; a short first run is joined by the one after it.
        var kept = new List<SpeechRun>(runs.Count);
        foreach (var run in runs)
        {
            if (kept.Count > 0 && (run.Length < MinSpeechFrames || kept[^1].Length < MinSpeechFrames))
            {
                kept[^1] = kept[^1] with
                {
                    End = run.End
                };
            }
            else
            {
                kept.Add(run);
            }
        }

        var cuts = new List<int>(Math.Max(0, kept.Count - 1));
        for (var k = 1; k < kept.Count; k++)
        {
            cuts.Add((kept[k - 1].End + kept[k].Start) / 2);
        }

        return cuts;
    }

    private static double FrameRms(ReadOnlySpan<byte> frame, int channels)
    {
        var blockAlign = channels * 2;
        var frames = frame.Length / blockAlign;
        double sum = 0;
        for (var f = 0; f < frames; f++)
        {
            var block = frame.Slice(f * blockAlign, blockAlign);
            double mono = 0;
            for (var c = 0; c < channels; c++)
            {
                mono += BinaryPrimitives.ReadInt16LittleEndian(block[(c * 2)..]);
            }

            mono /= channels * FullScale;
            sum += mono * mono;
        }

        return frames == 0 ? 0 : Math.Sqrt(sum / frames);
    }

    /// <summary>One stretch of the clip to transcribe on its own, in milliseconds from the start of the payload.</summary>
    /// <param name="StartMs">Inclusive start.</param>
    /// <param name="EndMs">Exclusive end.</param>
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Region(long StartMs, long EndMs);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SpeechRun(int Start, int End)
    {
        public int Length => End - Start;
    }
}
