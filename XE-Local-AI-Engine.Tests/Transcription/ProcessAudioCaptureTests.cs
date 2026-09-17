namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Buffers.Binary;
using NAudio.Wave;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The platform-independent half of Windows per-application capture: the documented build floor and the PCM
///     conversion chain. Everything here runs on every operating system, because on this project's Linux gate it is
///     the only coverage the slice has — the WASAPI half is <c>[RunOn(OS.Windows)]</c> and reports skipped.
/// </summary>
/// <remarks>
///     The converter is exercised against the <b>real</b> NAudio types rather than a mock: <c>BufferedWaveProvider</c>,
///     <c>StereoToMonoSampleProvider</c> and <c>WdlResamplingSampleProvider</c> live in <c>NAudio.Core</c>, are fully
///     managed and run anywhere, which is the entire reason the conversion was split out of the Windows source.
///     Inputs are synthesised in code; no fixture is committed.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ProcessAudioCaptureTests
{
    private const int TargetSampleRate = 16000;
    private const int SourceSampleRate = 44100;

    [Test]
    public void Support_RejectsBuild20347()
    {
        AssertEx.False(ProcessAudioCaptureSupport.IsBuildSupported(major: 10, build: 20347),
            "One build below the documented AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS floor must report unsupported.");
    }

    [Test]
    public void Support_AcceptsBuild20348()
    {
        AssertEx.True(ProcessAudioCaptureSupport.IsBuildSupported(major: 10, build: 20348),
            "Build 20348 is the floor Microsoft documents, so it is supported.");
        AssertEx.Equal(20348, ProcessAudioCaptureSupport.MinimumWindowsBuild,
            "Lowering the constant without a live observation on a 19041-20347 host is what §6 forbids.");
    }

    [Test]
    public void Support_Rejects19041_TheAnalyzerAnnotationIsNotTheFloor()
    {
        // NAudio annotates WithProcessLoopback [SupportedOSPlatform("windows10.0.19041.0")]. That number satisfies
        // CA1416; it is not compatibility evidence, and the capability must not be gated on it.
        AssertEx.False(ProcessAudioCaptureSupport.IsBuildSupported(major: 10, build: 19041),
            "19041 is the analyzer annotation, not the documented floor: gating there would promise a capability Microsoft does not document.");
    }

    [Test]
    public async Task NotSupportedSource_ReportsUnsupportedAndThrowsNamedError()
    {
        var source = new NotSupportedProcessAudioCaptureSource();

        AssertEx.False(source.IsSupported, "A host without WASAPI process loopback reports the capability as false.");
        AssertEx.Empty(await source.ListCandidatesAsync(CancellationToken.None),
            "The picker answers with an empty list rather than an error: having nothing to offer is a normal answer.");

        var failure = await AssertEx.ThrowsAsync<TranscriptionProcessCaptureNotSupportedException>(() => source.CaptureAsync(Guid.NewGuid(), processId: 4321, CancellationToken.None),
            "Capture must fail CLOSED with a named error; returning quietly would open a live session that silently receives no audio.");

        AssertEx.Contains(failure.Message, "4321", StringComparison.Ordinal,
            "The message names the process the caller asked for, so the failure is actionable.");
    }

    [Test]
    public void Converter_StereoFloat44100_ProducesMonoInt16At16k()
    {
        const float Amplitude = 0.5f;
        const int Frames = SourceSampleRate; // one second

        var converter = new Wasapi16kMonoPcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(SourceSampleRate, channels: 2));
        converter.Write(StereoFloatSine(Frames, frequencyHz: 440, Amplitude));
        var (pcm, terminated) = Drain(converter);

        AssertEx.True(terminated, "The drain terminated on its own, which is what ReadFully = false buys.");

        var samples = pcm.Length / sizeof(short);
        AssertEx.True(samples is > 15_000 and <= TargetSampleRate,
            $"One second of 44.1 kHz stereo must resample to about 16000 mono samples; got {samples}.");

        // Resampler output is implementation-defined sample by sample, so the assertion is on energy, not values.
        var expectedRms = Amplitude / MathF.Sqrt(2f) * 32768f;
        var actualRms = Rms(pcm);
        AssertEx.True(MathF.Abs(actualRms - expectedRms) < expectedRms * 0.1f,
            $"A downmixed, resampled sine keeps its energy; expected about {expectedRms:F0} RMS, got {actualRms:F0}.");
    }

    [Test]
    public void Converter_AlreadyMonoInt16At16k_PassesThrough()
    {
        // The chain degenerates to a format conversion when nothing needs downmixing or resampling, so the call
        // site needs no branch — but only if the round trip through float does not cost a least-significant bit.
        short[] expected = [0, 1, -1, 1000, -1000, short.MaxValue, short.MinValue, 12_345, -12_345];
        var input = new byte[expected.Length * sizeof(short)];
        for (var i = 0; i < expected.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * sizeof(short)), expected[i]);
        }

        var converter = new Wasapi16kMonoPcmConverter(new WaveFormat(TargetSampleRate, bits: 16, channels: 1));
        converter.Write(input);
        var (pcm, terminated) = Drain(converter);

        AssertEx.True(terminated, "The drain terminated on its own.");
        AssertEx.Equal(expected.Length, pcm.Length / sizeof(short), "Nothing is added or dropped on the passthrough path.");
        for (var i = 0; i < expected.Length; i++)
        {
            AssertEx.Equal(expected[i], BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * sizeof(short))),
                $"Sample {i} survived the int16 to float and back round trip unchanged.");
        }
    }

    [Test]
    public void Converter_ClampsOutOfRangeFloatSamples()
    {
        // WASAPI hands back IEEE float, which is not bounded to [-1, 1]. Without the clamp these wrap around and a
        // loud passage becomes noise of the opposite sign.
        float[] samples = [2f, -2f, 0f, 1f, -1f];
        var input = new byte[samples.Length * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(i * sizeof(float)), samples[i]);
        }

        var converter = new Wasapi16kMonoPcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, channels: 1));
        converter.Write(input);
        var (pcm, _) = Drain(converter);

        AssertEx.Equal(samples.Length, pcm.Length / sizeof(short), "Every input sample produced one output sample.");
        AssertEx.Equal(short.MaxValue, ReadSample(pcm, 0), "+2.0 clamps to the positive int16 bound, it does not wrap.");
        AssertEx.Equal(short.MinValue, ReadSample(pcm, 1), "-2.0 clamps to the negative int16 bound.");
        AssertEx.Equal((short)0, ReadSample(pcm, 2), "Silence stays silent.");
        AssertEx.Equal(short.MaxValue, ReadSample(pcm, 3), "Full scale is the positive bound, because int16 has no +32768.");
        AssertEx.Equal(short.MinValue, ReadSample(pcm, 4), "Negative full scale maps exactly.");
    }

    [Test]
    public void Converter_ReturnsZeroOnAnEmptyFollowUpRead()
    {
        // BufferedWaveProvider.ReadFully defaults to TRUE, which zero-fills an underflow and returns the full
        // requested count. This test fails outright if that default is ever left in place.
        var converter = new Wasapi16kMonoPcmConverter(new WaveFormat(TargetSampleRate, bits: 16, channels: 1));
        converter.Write(new byte[320]);

        var (first, terminated) = Drain(converter);

        AssertEx.True(terminated, "The first drain terminated rather than manufacturing silence forever.");
        AssertEx.NotEmpty(first, "The frame that was written came back out.");
        AssertEx.Equal(0, converter.Read(new byte[4096]),
            "A read against a starved chain returns zero, not a buffer of manufactured silence.");
    }

    [Test]
    public void Converter_TotalOutputIsBoundedByTotalInput()
    {
        const int Packets = 20;
        const int FramesPerPacket = SourceSampleRate / 10; // 100 ms, NAudio's default buffer length

        var converter = new Wasapi16kMonoPcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(SourceSampleRate, channels: 2));
        var total = 0;
        for (var packet = 0; packet < Packets; packet++)
        {
            converter.Write(StereoFloatSine(FramesPerPacket, frequencyHz: 220, amplitude: 0.25f));
            var (pcm, terminated) = Drain(converter);
            AssertEx.True(terminated, $"The drain after packet {packet} terminated instead of looping to the cap.");
            total += pcm.Length;
        }

        // 16 kHz mono int16 out against 44.1 kHz stereo float in: the output can never carry more samples than the
        // input did per channel. With ReadFully left at its default this bound is exceeded on the first packet.
        var bound = Packets * FramesPerPacket * sizeof(short);
        AssertEx.True(total <= bound,
            $"Total output must stay under the per-channel input sample count; got {total} bytes against a bound of {bound}.");
        AssertEx.True(total > bound / 4,
            $"The conversion must actually produce audio; got {total} bytes.");
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public void Candidates_OrAudioActivityAcrossTheSessionsOfOneProcess(bool firstActive, bool secondActive)
    {
        // Keeping the FIRST session seen made the answer depend on enumeration order: an idle session encountered
        // before a playing one reported a playing application as silent. Activity belongs to the process, not to
        // whichever of its sessions came back first, so both orderings must agree.
        var candidates = ProcessAudioCaptureCandidates.Aggregate([(1234, firstActive), (1234, secondActive)],
            static processId => $"app-{processId}");

        AssertEx.Equal(1, candidates.Count, "One process is one row however many sessions it holds.");
        AssertEx.True(candidates[0].HasAudio, "One active session is enough for the process to count as playing.");
    }

    [Test]
    public void Candidates_ReportNoAudioWhenEverySessionOfTheProcessIsIdle()
    {
        // The control for the test above: OR must not collapse into "always true".
        var candidates = ProcessAudioCaptureCandidates.Aggregate([(1234, false), (1234, false)],
            static processId => $"app-{processId}");

        AssertEx.Equal(1, candidates.Count, "Still one row.");
        AssertEx.False(candidates[0].HasAudio, "A process with only idle sessions is not playing.");
    }

    [Test]
    public void Candidates_DropNonPositiveProcessIdsAndOrderByName()
    {
        // WithProcessLoopback takes a uint, so a zero or negative id is not a process capture could ever target.
        var candidates = ProcessAudioCaptureCandidates.Aggregate([(0, true), (-1, true), (30, true), (10, false), (20, true)],
            static processId => processId switch
            {
                30 => "alpha",
                10 => "Bravo",
                _ => "charlie"
            });

        AssertEx.Equal(3, candidates.Count, "The two unusable ids are dropped, the three real ones are kept.");
        AssertEx.Equal("alpha,Bravo,charlie", string.Join(",", candidates.Select(candidate => candidate.Name)),
            "Rows are ordered by name, case-insensitively, so the picker reads alphabetically.");
        AssertEx.Equal(30, candidates[0].ProcessId, "The name resolver is applied per distinct process id.");
    }

    private static short ReadSample(byte[] pcm, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * sizeof(short)));

    private static byte[] StereoFloatSine(int frames, double frequencyHz, float amplitude)
    {
        var bytes = new byte[frames * 2 * sizeof(float)];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * frame / SourceSampleRate));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(((frame * 2) + 0) * sizeof(float)), sample);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(((frame * 2) + 1) * sizeof(float)), sample);
        }

        return bytes;
    }

    private static float Rms(byte[] pcm)
    {
        var samples = pcm.Length / sizeof(short);
        if (samples == 0)
        {
            return 0f;
        }

        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            double value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * sizeof(short)));
            sum += value * value;
        }

        return (float)Math.Sqrt(sum / samples);
    }

    /// <summary>
    ///     Drains the converter the way the capture pump does, with a hard iteration cap so a converter that never
    ///     starves FAILS the test instead of hanging the gate.
    /// </summary>
    private static (byte[] Pcm, bool Terminated) Drain(Wasapi16kMonoPcmConverter converter, int maxIterations = 2_000)
    {
        using var output = new MemoryStream();
        var chunk = new byte[4096];
        for (var i = 0; i < maxIterations; i++)
        {
            var written = converter.Read(chunk);
            if (written == 0)
            {
                return (output.ToArray(), true);
            }

            output.Write(chunk, 0, written);
        }

        return (output.ToArray(), false);
    }
}
