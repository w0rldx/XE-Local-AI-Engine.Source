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
