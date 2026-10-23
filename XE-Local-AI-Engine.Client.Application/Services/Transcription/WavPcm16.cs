namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Buffers.Binary;
using System.Runtime.InteropServices;

/// <summary>
///     Wraps raw 16-bit PCM in the canonical 44-byte RIFF/WAVE header the transcription runtime accepts, finds the PCM
///     payload of an uploaded WAV, and converts byte counts to audio time.
/// </summary>
/// <remarks>
///     Written by hand rather than taken from a library: the brief rules out NAudio on this path, nothing in the repository
///     writes a RIFF header today, and the whole format is four little-endian integers around a fixed preamble — a dependency
///     for forty-four bytes would cost more to license-check than to write. <see cref="BytesPerMillisecond" /> is the single
///     conversion constant between a byte count and audio time for the live path, and every millisecond the segmenter reasons
///     about derives from it, never from a wall clock, which is what makes a live session's timestamps reproducible.
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
    private const int ExtensibleFmtLength = 40;
    private const int ExtensibleFormatTag = 0xFFFE;

    // An upload is untrusted: header values outside these bounds are refused before they size any buffer.
    private const int MinSampleRate = 8_000;
    private const int MaxSampleRate = 192_000;
    private const int MaxChannels = 8;

    // KSDATAFORMAT_SUBTYPE_PCM after its leading format tag: the remaining fourteen bytes of the GUID.
    private static ReadOnlySpan<byte> PcmSubFormatTail => [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>Prefixes <paramref name="pcm" /> with a 44-byte RIFF/WAVE header describing 16-bit mono PCM.</summary>
    /// <param name="pcm">The raw little-endian int16 samples.</param>
    /// <param name="sampleRate">Samples per second; the header's rate and byte-rate fields are derived from it.</param>
    public static byte[] Wrap(ReadOnlySpan<byte> pcm, int sampleRate = SampleRate)
    {
        var wav = new byte[HeaderLength + pcm.Length];
        WriteHeader(wav, pcm.Length, sampleRate, Channels);
        pcm.CopyTo(wav.AsSpan(HeaderLength));
        return wav;
    }

    /// <summary>Writes the 44-byte header for <paramref name="pcmLength" /> bytes of interleaved 16-bit PCM.</summary>
    /// <param name="header">At least <see cref="HeaderLength" /> bytes; the payload follows them.</param>
    /// <param name="pcmLength">The payload length in bytes.</param>
    /// <param name="sampleRate">Samples per second per channel.</param>
    /// <param name="channels">Interleaved channel count.</param>
    public static void WriteHeader(Span<byte> header, int pcmLength, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(pcmLength);

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(HeaderLength - 8 + pcmLength));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], FmtChunkLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], PcmFormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * channels * BytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * BytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], BytesPerSample * 8);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)pcmLength);
    }

    /// <summary>
    ///     Reads where the 16-bit PCM payload of a RIFF/WAVE stream lives, or <see langword="null" /> when the stream is
    ///     not 16-bit integer PCM (plain or WAVE_FORMAT_EXTENSIBLE) or the header is malformed.
    /// </summary>
    /// <remarks>
    ///     Walks the chunks from the start of the seekable <paramref name="stream" /> and leaves it positioned anywhere. A
    ///     data length larger than the bytes present (a streaming writer's placeholder) is clamped to what is there, so
    ///     the payload never reaches past the end of the stream. Rates outside 8-192 kHz and more than eight channels
    ///     are refused.
    /// </remarks>
    public static Layout? TryReadLayout(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;
        Span<byte> riff = stackalloc byte[12];
        if (stream.ReadAtLeast(riff, riff.Length, throwOnEndOfStream: false) < riff.Length
            || !riff[..4].SequenceEqual("RIFF"u8)
            || !riff[8..].SequenceEqual("WAVE"u8))
        {
            return null;
        }

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[ExtensibleFmtLength];
        var sampleRate = 0;
        var channels = 0;
        while (stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
        {
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var body = stream.Position;
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                var want = (int)Math.Min(size, ExtensibleFmtLength);
                var read = stream.ReadAtLeast(fmt[..want], want, throwOnEndOfStream: false);
                if (!IsPcm16(fmt[..read], out sampleRate, out channels))
                {
                    return null;
                }
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    return null;
                }

                var blockAlign = channels * BytesPerSample;
                var length = Math.Min(size, stream.Length - body);
                length -= length % blockAlign;
                return length > 0 ? new Layout(sampleRate, channels, body, length) : null;
            }

            stream.Position = body + size + (size & 1);
        }

        return null;
    }

    private static bool IsPcm16(ReadOnlySpan<byte> fmt, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        if (fmt.Length < FmtChunkLength)
        {
            return false;
        }

        int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        if (tag == ExtensibleFormatTag)
        {
            if (fmt.Length < ExtensibleFmtLength || !fmt[26..].SequenceEqual(PcmSubFormatTail))
            {
                return false;
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        sampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]), int.MaxValue);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        return tag == PcmFormatTag
               && bits == BytesPerSample * 8
               && channels is >= 1 and <= MaxChannels
               && sampleRate is >= MinSampleRate and <= MaxSampleRate
               && blockAlign == channels * BytesPerSample;
    }

    /// <summary>How many whole milliseconds of audio <paramref name="pcmByteCount" /> raw PCM bytes carry.</summary>
    /// <remarks>
    ///     Truncating is deliberate and the remainder is the caller's to keep: the live path holds a cumulative byte
    ///     count and re-derives the time from it each push, so bytes that do not complete a millisecond count towards
    ///     the next one instead of being rounded away per frame.
    /// </remarks>
    public static long DurationMs(long pcmByteCount) =>
        pcmByteCount / BytesPerMillisecond;

    /// <summary>Where a WAV file's interleaved 16-bit PCM payload lives and how it is laid out.</summary>
    /// <param name="SamplesPerSecond">Samples per second per channel.</param>
    /// <param name="ChannelCount">Interleaved channel count.</param>
    /// <param name="DataOffset">The byte offset of the first sample in the file.</param>
    /// <param name="DataLength">The payload length in bytes, a whole number of sample frames.</param>
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Layout(int SamplesPerSecond, int ChannelCount, long DataOffset, long DataLength)
    {
        /// <summary>Bytes per sample frame across all channels.</summary>
        public int BlockAlign => ChannelCount * BytesPerSample;
    }
}
