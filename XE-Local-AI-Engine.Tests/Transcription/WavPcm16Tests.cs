namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Buffers.Binary;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the RIFF/WAVE header the live path hands the transcription runtime, field by field.
/// </summary>
/// <remarks>
///     The header is read by a C++ multipart parser in another process, so a wrong length field or a swapped byte
///     order fails somewhere with no useful message. These tests parse the bytes back rather than comparing against a
///     recorded blob, so a failure names the field that moved.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class WavPcm16Tests
{
    [Test]
    public void Wrap_WritesTheCanonical44ByteHeaderForMono16k()
    {
        var wav = WavPcm16.Wrap(Pcm(320));

        AssertEx.Equal(44, WavPcm16.HeaderLength, "The canonical RIFF/WAVE header for PCM is forty-four bytes.");
        AssertEx.Equal(44 + 320, wav.Length, "The payload follows the header untouched.");
        AssertEx.Equal("RIFF", Ascii(wav, 0, 4), "Every WAVE file opens with the RIFF container magic.");
        AssertEx.Equal("WAVE", Ascii(wav, 8, 4), "The RIFF form type says this is audio, not an AVI.");
        AssertEx.Equal("fmt ", Ascii(wav, 12, 4), "The format chunk is first and its identifier is space-padded.");
        AssertEx.Equal("data", Ascii(wav, 36, 4), "The data chunk header sits immediately before the payload.");
    }

    [Test]
    public void Wrap_LengthFieldsMatchThePayload()
    {
        const int PayloadLength = 1_024;

        var wav = WavPcm16.Wrap(Pcm(PayloadLength));

        AssertEx.Equal((uint)(36 + PayloadLength),
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)),
            "The RIFF size counts everything after the first eight bytes.");
        AssertEx.Equal((uint)PayloadLength,
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)),
            "The data size is the payload alone; a header-inclusive value makes the reader run off the end.");
    }

    [Test]
    public void Wrap_RoundTripsThroughBinaryPrimitives()
    {
        var pcm = Pcm(64);

        var wav = WavPcm16.Wrap(pcm);

        AssertEx.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16)), "A PCM format chunk is sixteen bytes long.");
        AssertEx.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)), "Format tag one is uncompressed PCM.");
        AssertEx.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), "The live path is mono.");
        AssertEx.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), "The sample rate is the one the engine captures at.");
        AssertEx.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)), "Byte rate is rate x channels x two bytes.");
        AssertEx.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32)), "One mono frame is two bytes wide.");
        AssertEx.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), "Samples are sixteen bits.");
        AssertEx.True(wav.AsSpan(44).SequenceEqual(pcm), "The samples reach the runtime byte for byte.");
    }

    [Test]
    public void Wrap_HonoursANonDefaultSampleRate()
    {
        // The default is the only rate the engine captures at, but the header must describe whatever it was handed:
        // a rate written as a constant would mislabel any future resampling path instead of failing loudly.
        var wav = WavPcm16.Wrap(Pcm(32), sampleRate: 8_000);

        AssertEx.Equal(8_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), "The header carries the rate it was given.");
        AssertEx.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)), "Byte rate follows the given rate, not the default.");
    }

    [Test]
    [Arguments(0L, 0L, "no audio at all")]
    [Arguments(32L, 1L, "exactly one millisecond")]
    [Arguments(31L, 0L, "one byte short of a millisecond, which must not round up")]
    [Arguments(16_000L, 500L, "half a second")]
    [Arguments(352_000L, 11_000L, "the jfk.wav fixture's eleven-second payload")]
    public void DurationMs_ConvertsBytesAtThirtyTwoBytesPerMillisecond(long byteCount, long expectedMs, string because)
    {
        AssertEx.Equal(32, WavPcm16.BytesPerMillisecond, "Thirty-two bytes per millisecond is the live path's only clock.");
        AssertEx.Equal(expectedMs, WavPcm16.DurationMs(byteCount), $"Expected {expectedMs} ms for {because}.");
    }

    [Test]
    public void TryReadLayout_Mono16k_FindsThePayloadAfterTheCanonicalHeader()
    {
        var wav = WavPcm16.Wrap(Pcm(640));

        var layout = Require(wav, "A canonical 16-bit mono WAV is readable.");

        AssertEx.Equal(new WavPcm16.Layout(16_000, 1, 44, 640), layout);
    }

    [Test]
    public void TryReadLayout_Stereo44k_WalksPastAnOddSizedChunkToTheData()
    {
        // A LIST chunk with an odd length is followed by one pad byte; a reader that ignores the pad lands one byte
        // short of the next chunk id and never finds "data".
        var wav = Build(Fmt(formatTag: 1, channels: 2, sampleRate: 44_100, bits: 16), extraChunk: [1, 2, 3], payload: Pcm(400));

        var layout = Require(wav, "A stereo 44.1 kHz WAV with a metadata chunk is readable.");

        AssertEx.Equal(44_100, layout.SamplesPerSecond);
        AssertEx.Equal(2, layout.ChannelCount);
        AssertEx.Equal(4, layout.BlockAlign, "Two channels of two bytes make a four-byte sample frame.");
        AssertEx.Equal(400L, layout.DataLength);
        AssertEx.Equal((byte)0, wav[(int)layout.DataOffset], "The payload starts where the first PCM byte was written.");
        AssertEx.Equal((byte)1, wav[(int)layout.DataOffset + 1], "The payload offset accounts for the pad byte.");
    }

    [Test]
    public void TryReadLayout_ExtensiblePcm_IsReadable()
    {
        var wav = Build(Extensible(subFormatTag: 1), extraChunk: null, payload: Pcm(320));

        var layout = Require(wav, "WAVE_FORMAT_EXTENSIBLE with the PCM subformat is plain PCM.");

        AssertEx.Equal(48_000, layout.SamplesPerSecond);
        AssertEx.Equal(320L, layout.DataLength);
    }

    [Test]
    public void TryReadLayout_DataSizeLargerThanTheFile_IsClampedToTheBytesPresent()
    {
        // A streaming writer that never patched its header leaves 0xFFFFFFFF there.
        var wav = WavPcm16.Wrap(Pcm(322));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), uint.MaxValue);

        var layout = Require(wav, "A placeholder data size must not make the file unreadable.");

        AssertEx.Equal(322L, layout.DataLength);
        AssertEx.True(layout.DataOffset + layout.DataLength <= wav.Length, "The payload never reaches past the end of the file.");
    }

    [Test]
    [Arguments(3, 32, "32-bit float")]
    [Arguments(1, 24, "24-bit integer")]
    [Arguments(1, 8, "8-bit integer")]
    [Arguments(6, 8, "A-law")]
    public void TryReadLayout_NotSixteenBitPcm_IsNull(int formatTag, int bits, string because)
    {
        var wav = Build(Fmt(formatTag, channels: 1, sampleRate: 16_000, bits), extraChunk: null, payload: Pcm(96));

        AssertEx.Null(Read(wav), $"{because} is not measured; the caller sends the file in one request.");
    }

    [Test]
    [Arguments(384_000, 1, "a rate above 192 kHz")]
    [Arguments(4_000, 1, "a rate below 8 kHz")]
    [Arguments(16_000, 32_767, "32767 channels")]
    [Arguments(16_000, 9, "nine channels")]
    public void TryReadLayout_HeaderOutsideTheBounds_IsNull(int sampleRate, int channels, string because)
    {
        // An upload is untrusted: a tiny file declaring 384 kHz and 32767 channels would otherwise size a 500 MB frame.
        var wav = Build(Fmt(formatTag: 1, channels, sampleRate, bits: 16), extraChunk: null, payload: Pcm(channels * 2));

        AssertEx.Null(Read(wav), $"A header with {because} must be refused before it sizes any buffer.");
    }

    [Test]
    public void TryReadLayout_UpperBounds_AreAccepted()
    {
        // The control for the refusals above: 192 kHz with eight channels is still a real recording.
        var wav = Build(Fmt(formatTag: 1, channels: 8, sampleRate: 192_000, bits: 16), extraChunk: null, payload: Pcm(160));

        var layout = Require(wav, "192 kHz eight-channel PCM is within the bounds.");

        AssertEx.Equal(192_000, layout.SamplesPerSecond);
        AssertEx.Equal(8, layout.ChannelCount);
    }

    [Test]
    public void TryReadLayout_ExtensibleFloat_IsNull()
    {
        var wav = Build(Extensible(subFormatTag: 3), extraChunk: null, payload: Pcm(96));

        AssertEx.Null(Read(wav), "WAVE_FORMAT_EXTENSIBLE carrying float samples is not 16-bit PCM.");
    }

    [Test]
    public void TryReadLayout_TruncatedOrForeignBytes_IsNull()
    {
        AssertEx.Null(Read(TranscriptionAudioFixtures.Wav), "A header with no format body and no data chunk is unreadable.");
        AssertEx.Null(Read(TranscriptionAudioFixtures.Ogg), "An Ogg file is not RIFF.");
        AssertEx.Null(Read(WavPcm16.Wrap([])), "A WAV with no samples has nothing to measure.");
    }

    private static WavPcm16.Layout Require(byte[] wav, string message)
    {
        var layout = Read(wav);
        AssertEx.True(layout.HasValue, message);
        return layout.GetValueOrDefault();
    }

    private static WavPcm16.Layout? Read(byte[] wav)
    {
        using var stream = new MemoryStream(wav, writable: false);
        return WavPcm16.ReadLayoutOrNull(stream);
    }

    private static byte[] Fmt(int formatTag, int channels, int sampleRate, int bits)
    {
        var fmt = new byte[16];
        var blockAlign = channels * bits / 8;
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        return fmt;
    }

    private static byte[] Extensible(int subFormatTag)
    {
        var fmt = new byte[40];
        var bits = subFormatTag == 3 ? 32 : 16;
        Fmt(0xFFFE, channels: 2, sampleRate: 48_000, bits).CopyTo(fmt, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), (ushort)subFormatTag);
        byte[] guidTail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
        guidTail.CopyTo(fmt, 26);
        return fmt;
    }

    private static byte[] Build(byte[] fmt, byte[]? extraChunk, byte[] payload)
    {
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        output.Write(new byte[4]);
        output.Write("WAVE"u8);
        WriteChunk(output, "fmt "u8, fmt);
        if (extraChunk is not null)
        {
            WriteChunk(output, "LIST"u8, extraChunk);
        }

        WriteChunk(output, "data"u8, payload);
        return output.ToArray();
    }

    private static void WriteChunk(MemoryStream output, ReadOnlySpan<byte> id, byte[] body)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        output.Write(id);
        output.Write(size);
        output.Write(body);
        if (body.Length % 2 == 1)
        {
            output.WriteByte(0);
        }
    }

    private static string Ascii(byte[] wav, int offset, int length) =>
        Encoding.ASCII.GetString(wav, offset, length);

    private static byte[] Pcm(int length)
    {
        var pcm = new byte[length];
        for (var index = 0; index < pcm.Length; index++)
        {
            pcm[index] = (byte)(index % 251);
        }

        return pcm;
    }
}
