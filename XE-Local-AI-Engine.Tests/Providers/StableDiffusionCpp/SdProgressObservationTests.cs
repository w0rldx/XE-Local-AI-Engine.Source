namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins how sd-server's console output is framed and read. Both halves are derived from a real capture: the pinned
///     <c>sd-server</c> was run against the installed SD1.5 file-set and its merged stdout/stderr hexdumped through a
///     whole generation, so every literal in this file is a byte sequence the daemon actually emitted rather than a
///     guess at its format.
/// </summary>
public sealed class SdProgressObservationTests
{
    private const string Esc = "\u001b[K";

    /// <summary>
    ///     The exact shape of the sampler bar in the capture. The leading carriage return and the trailing erase
    ///     sequence are the whole point — see <see cref="SdOutputFrameSplitter" />.
    /// </summary>
    private const string SamplerFrames =
        "\r  |======>                     | 1/8 - 6.34s/it" + Esc
                                                            + "\r  |============>               | 2/8 - 4.97s/it" + Esc;

    [Test]
    public void Parse_SamplerStepLine_ReportsSamplingWithCounters()
    {
        var parsed = SdProgressLineParser.TryParse("  |======>       | 12/20 - 4.97s/it", out var observation);

        AssertEx.True(parsed, "The sampler step bar is the one line that carries real progress.");
        AssertEx.Equal(ImageGenPhase.Sampling, observation!.Phase);
        AssertEx.Equal(expected: 12, observation.Step);
        AssertEx.Equal(expected: 20, observation.TotalSteps);
        AssertEx.Equal(expected: 4.97, observation.SecondsPerIteration);
    }

    /// <summary>Above one iteration per second sd.cpp flips the unit; the consumer must never see two rate scales.</summary>
    [Test]
    public void Parse_SamplerStepLineInIterationsPerSecond_NormalizesToSeconds()
    {
        var parsed = SdProgressLineParser.TryParse("  |===>          | 4/20 - 2.00it/s", out var observation);

        AssertEx.True(parsed);
        AssertEx.Equal(ImageGenPhase.Sampling, observation!.Phase);
        AssertEx.Equal(expected: 0.5, observation.SecondsPerIteration);
    }

    /// <summary>
    ///     THE decoy. sd.cpp prints this once per generation and, at the default batch count of one, it reads
    ///     <c>1/1</c>. A parser anchored on the fraction rather than the rate token reports "step 1 of 1" and slams the
    ///     bar to 100% before a single step has run.
    /// </summary>
    [Test]
    public void Parse_BatchCounterLine_IsNeverReadAsASamplerStep()
    {
        var parsed = SdProgressLineParser.TryParse("[INFO ] stable-diffusion.cpp:4590 - generating image: 1/1 - seed 42", out var observation);

        AssertEx.False(parsed && observation!.Phase == ImageGenPhase.Sampling,
            "'generating image: 1/1 - seed 42' carries no step progress; reading it as one flashes the bar to complete.");
    }

    /// <summary>The tensor-loader bar also carries an N/M pair, but of tensors — it is a load, not a step.</summary>
    [Test]
    public void Parse_TensorLoaderBar_ReportsLoadingWithoutStepCounters()
    {
        var parsed = SdProgressLineParser.TryParse("  |#############   | 227/686 - 951.68MB/s", out var observation);

        AssertEx.True(parsed);
        AssertEx.Equal(ImageGenPhase.Loading, observation!.Phase);
        AssertEx.Null(observation.Step, "686 is a tensor count, not a sampling step total.");
    }

    [Test]
    public void Parse_PhaseMarkerLines_MapToTheirPhases()
    {
        AssertPhase("[INFO ] stable-diffusion.cpp:1119 - loading model from '/models/sd15.gguf'", ImageGenPhase.Loading);
        AssertPhase("[DEBUG] model_loader.cpp:999  - loading 196/1131 tensors from /models/sd15.gguf", ImageGenPhase.Loading);
        AssertPhase("[INFO ] stable-diffusion.cpp:4270 - get_learned_condition completed, taking 0.25s", ImageGenPhase.Encoding);
        AssertPhase("[INFO ] stable-diffusion.cpp:4622 - sampling completed, taking 41.54s", ImageGenPhase.Decoding);
        AssertPhase("[INFO ] stable-diffusion.cpp:4319 - decode_first_stage completed, taking 7.27s", ImageGenPhase.Decoding);
    }

    [Test]
    public void Parse_UnrelatedLine_ReportsNothing()
    {
        AssertEx.False(SdProgressLineParser.TryParse("[INFO ] main.cpp:148  - listening on: http://127.0.0.1:45999", out _));
        AssertEx.False(SdProgressLineParser.TryParse(line: null, out _));
        AssertEx.False(SdProgressLineParser.TryParse("   ", out _));
    }

    /// <summary>
    ///     The framing fixture. Feeding the capture's own bytes must yield BOTH sampler frames — the second one arrives
    ///     with no trailing newline anywhere, so a reader that waits for CR/LF would still be holding it.
    /// </summary>
    [Test]
    public void Splitter_LeadingCarriageReturnFrames_EmitsEachFrameAsItIsWritten()
    {
        var frames = new List<string>();
        var splitter = new SdOutputFrameSplitter(frames.Add);

        splitter.Append(SamplerFrames);

        AssertEx.Equal(expected: 2, frames.Count, "Each erase-terminated bar frame must be emitted as it is written, not one behind.");
        AssertEx.Contains(frames[0], "1/8 - 6.34s/it");
        AssertEx.Contains(frames[1], "2/8 - 4.97s/it");
        AssertEx.False(frames[1].Contains('\u001b', StringComparison.Ordinal), "The erase sequence terminates the frame and is not part of its text.");
    }

    /// <summary>A frame split across two reads is reassembled, not truncated into two half-frames.</summary>
    [Test]
    public void Splitter_FrameSplitAcrossReads_IsReassembled()
    {
        var frames = new List<string>();
        var splitter = new SdOutputFrameSplitter(frames.Add);

        splitter.Append("\r  |====>   | 3/8 - 5.0");
        splitter.Append("6s/it" + Esc);

        AssertEx.Equal(expected: 1, frames.Count);
        AssertEx.Contains(frames[0], "3/8 - 5.06s/it");
    }

    [Test]
    public void Splitter_FlushAtEndOfStream_EmitsAnUnterminatedTail()
    {
        var frames = new List<string>();
        var splitter = new SdOutputFrameSplitter(frames.Add);

        splitter.Append("[INFO ] main.cpp:148  - listening on: http://127.0.0.1:45999");
        AssertEx.Empty(frames, "An unterminated tail waits for more bytes while the process is alive.");

        splitter.Flush();
        AssertEx.Equal(expected: 1, frames.Count, "At end of stream the tail must still be forwarded.");
    }

    /// <summary>End to end: the captured frames, split and parsed, are two ascending sampler steps.</summary>
    [Test]
    public void SplitterAndParser_OverTheCapturedFrames_YieldAscendingSamplerSteps()
    {
        var observations = new List<SdProgressObservation>();
        var splitter = new SdOutputFrameSplitter(frame =>
        {
            if (SdProgressLineParser.TryParse(frame, out var observation))
            {
                observations.Add(observation);
            }
        });

        splitter.Append(SamplerFrames);
        splitter.Flush();

        AssertEx.Equal(expected: 2, observations.Count);
        AssertEx.Equal(expected: 1, observations[0].Step);
        AssertEx.Equal(expected: 2, observations[1].Step);
    }

    private static void AssertPhase(string line, ImageGenPhase expected)
    {
        var parsed = SdProgressLineParser.TryParse(line, out var observation);
        AssertEx.True(parsed, $"'{line}' must be recognized as a phase marker.");
        AssertEx.Equal(expected, observation!.Phase);
    }
}
