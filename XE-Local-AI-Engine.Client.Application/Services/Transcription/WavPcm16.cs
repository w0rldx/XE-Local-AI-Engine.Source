namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Buffers.Binary;

/// <summary>
///     Wraps raw 16-bit mono PCM in the canonical 44-byte RIFF/WAVE header the transcription runtime accepts, and
///     converts byte counts to audio time.
/// </summary>
/// <remarks>
///     <para>
///         Written by hand rather than taken from a library: the brief rules out NAudio on this path, nothing in the
///         repository writes a RIFF header today, and the whole format is four little-endian integers around a fixed
///         preamble. A dependency for forty-four bytes would cost more to license-check than to write.
///     </para>
///     <para>
///         <see cref="BytesPerMillisecond" /> is the single conversion constant between a byte count and audio time
///         for the live path. Every millisecond the segmenter reasons about is derived from it, never from a wall
///         clock, which is what makes a live session's timestamps reproducible.
///     </para>
/// </remarks>
public static class WavPcm16
{
    /// <summary>The one sample rate the live path speaks; whisper resamples anything else, so the engine never sends it.</summary>
    public const int SampleRate = 16_000;

    /// <summary>Two bytes per sample at 16 kHz mono, so one millisecond of audio is thirty-two bytes.</summary>
    public const int BytesPerMillisecond = (SampleRate * BytesPerSample) / 1000;

    /// <summary>The length of the canonical RIFF/WAVE header this writes, before the payload.</summary>
    public const int HeaderLength = 44;

    private const int BytesPerSample = 2;
    private const int Channels = 1;
    private const int PcmFormatTag = 1;
    private const int FmtChunkLength = 16;

    /// <summary>Prefixes <paramref name="pcm" /> with a 44-byte RIFF/WAVE header describing 16-bit mono PCM.</summary>
    /// <param name="pcm">The raw little-endian int16 samples.</param>
    /// <param name="sampleRate">Samples per second; the header's rate and byte-rate fields are derived from it.</param>
    public static byte[] Wrap(ReadOnlySpan<byte> pcm, int sampleRate = SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var wav = new byte[HeaderLength + pcm.Length];
        var header = wav.AsSpan(0, HeaderLength);

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(HeaderLength - 8 + pcm.Length));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], FmtChunkLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], PcmFormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * Channels * BytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], Channels * BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], BytesPerSample * 8);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)pcm.Length);

        pcm.CopyTo(wav.AsSpan(HeaderLength));
        return wav;
    }

    /// <summary>How many whole milliseconds of audio <paramref name="pcmByteCount" /> raw PCM bytes carry.</summary>
    /// <remarks>
    ///     Truncating is deliberate and the remainder is the caller's to keep: the live path holds a cumulative byte
    ///     count and re-derives the time from it each push, so bytes that do not complete a millisecond count towards
    ///     the next one instead of being rounded away per frame.
    /// </remarks>
    public static long DurationMs(long pcmByteCount) =>
        pcmByteCount / BytesPerMillisecond;
}
