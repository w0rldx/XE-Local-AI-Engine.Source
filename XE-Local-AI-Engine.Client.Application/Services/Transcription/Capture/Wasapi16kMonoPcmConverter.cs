namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>
///     Turns whatever format WASAPI hands back into the 16 kHz mono int16 PCM the live segmenter accepts. Written
///     against <c>NAudio.Core</c> only, so it is fully managed and is the one part of per-application capture the CI
///     gate can execute.
/// </summary>
/// <remarks>
///     The chain is <see cref="BufferedWaveProvider" /> → <c>ToSampleProvider()</c> → <see cref="StereoToMonoSampleProvider" />
///     (stereo sources only) → <see cref="WdlResamplingSampleProvider" /> (differing rates only), so a source that is already
///     16 kHz mono degenerates to a format conversion and no call site needs a branch.
///     <see cref="BufferedWaveProvider.ReadFully" /> is <see langword="false" /> deliberately: its <see langword="true" /> default
///     zero-fills an underflow, so a <c>while (Read(buffer) &gt; 0)</c> drain would never terminate and would manufacture unlimited silent PCM.
/// </remarks>
internal sealed class Wasapi16kMonoPcmConverter
{
    /// <summary>What the segmenter accepts, and the only rate this converter ever emits.</summary>
    internal const int TargetSampleRate = 16000;

    // int16 is symmetric about neither bound, so the scale is 32768 and the clamp is asymmetric. Scaling by 32767 instead costs
    // one least-significant bit on an already 16 kHz mono int16 source, which is the passthrough case this must not degrade.
    private const float Int16Scale = 32768f;
    private const float Int16Min = short.MinValue;
    private const float Int16Max = short.MaxValue;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _pipeline;
    private float[] _samples = [];

    /// <summary>Builds the conversion chain for one capture session's source format.</summary>
    /// <exception cref="NotSupportedException">The source carries more than two channels.</exception>
    internal Wasapi16kMonoPcmConverter(WaveFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        // StereoToMonoSampleProvider downmixes two channels only; more would need a real multichannel downmix. WASAPI process
        // loopback hands back NAudio's 44.1 kHz stereo float default, so this fails loudly rather than interleaving channels.
        if (sourceFormat.Channels > 2)
        {
            throw new NotSupportedException($"Per-application capture handles mono and stereo sources; this one carries {sourceFormat.Channels} channels.");
        }

        _buffer = new BufferedWaveProvider(sourceFormat)
        {
            // See the class remarks: the default `true` makes a drain loop non-terminating.
            ReadFully = false,

            // An overflow must throw rather than quietly drop the newest audio: the pump drains after every packet,
            // so a full buffer means the conversion stopped keeping up and the session should fail visibly.
            DiscardOnBufferOverflow = false
        };

        var pipeline = _buffer.ToSampleProvider();
        if (pipeline.WaveFormat.Channels == 2)
        {
            pipeline = new StereoToMonoSampleProvider(pipeline);
        }

        if (pipeline.WaveFormat.SampleRate != TargetSampleRate)
        {
            // Fully managed and Linux-capable, unlike MediaFoundationResampler, which is why the conversion is
            // testable on the CI runner at all.
            pipeline = new WdlResamplingSampleProvider(pipeline, TargetSampleRate);
        }

        _pipeline = pipeline;
    }

    /// <summary>Queues one source-format frame. Returns immediately; nothing is converted until <see cref="Read" />.</summary>
    internal void Write(ReadOnlySpan<byte> sourceFrame)
    {
        if (!sourceFrame.IsEmpty)
        {
            _buffer.AddSamples(sourceFrame);
        }
    }

    /// <summary>
    ///     Reads converted 16 kHz mono int16 PCM. Returns the number of bytes written, and <c>0</c> only when the
    ///     chain is genuinely starved — which is what makes a drain loop terminate.
    /// </summary>
    internal int Read(Span<byte> destination16kMonoInt16)
    {
        var sampleCount = destination16kMonoInt16.Length / sizeof(short);
        if (sampleCount == 0)
        {
            return 0;
        }

        if (_samples.Length < sampleCount)
        {
            _samples = new float[sampleCount];
        }

        var read = _pipeline.Read(_samples.AsSpan(0, sampleCount));
        for (var i = 0; i < read; i++)
        {
            var scaled = Math.Clamp(_samples[i] * Int16Scale, Int16Min, Int16Max);
            BinaryPrimitives.WriteInt16LittleEndian(destination16kMonoInt16[(i * sizeof(short))..], (short)scaled);
        }

        return read * sizeof(short);
    }
}
