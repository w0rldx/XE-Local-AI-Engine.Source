namespace XE_Local_AI_Engine.Tests.Providers.Image;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Hand-built minimal JPEG headers: SOI, an APP0 segment to walk past, then a frame header.</summary>
[Category(TestCategories.Unit)]
public sealed class JpegImageDimensionsTests
{
    [Test]
    [Arguments((byte)0xC0)]
    [Arguments((byte)0xC1)]
    [Arguments((byte)0xC2)]
    public void TryRead_HuffmanFrame_ReturnsDimensionsAndComponents(byte frameMarker)
    {
        var bytes = Jpeg(frameMarker, width: 1920, height: 1080, components: 3);

        AssertEx.True(JpegImageDimensions.TryRead(bytes, out var width, out var height, out var components));
        AssertEx.Equal(expected: 1920, width);
        AssertEx.Equal(expected: 1080, height);
        AssertEx.Equal(expected: 3, components);
    }

    [Test]
    public void TryRead_CmykFrame_ReportsFourComponents()
    {
        AssertEx.True(JpegImageDimensions.TryRead(Jpeg(0xC0, width: 64, height: 32, components: 4), out _, out _, out var components));
        AssertEx.Equal(expected: 4, components);
    }

    [Test]
    public void TryRead_FillBytesBeforeTheFrameMarker_AreSkipped()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20, 0x03, .. new byte[9]];

        AssertEx.True(JpegImageDimensions.TryRead(bytes, out var width, out var height, out _));
        AssertEx.Equal(expected: 32, width);
        AssertEx.Equal(expected: 16, height);
    }

    [Test]
    public void TryRead_PngBytes_IsRejected()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        AssertRejected(png);
    }

    [Test]
    public void TryRead_TruncatedFrameHeader_IsRejected()
    {
        var bytes = Jpeg(0xC0, width: 64, height: 64, components: 3);

        AssertRejected(bytes.AsSpan(0, bytes.Length - 10).ToArray());
    }

    [Test]
    public void TryRead_ScanBeforeAnyFrame_IsRejected()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0xD9];

        AssertRejected(bytes);
    }

    [Test]
    public void TryRead_EndOfImageBeforeAnyFrame_IsRejected()
    {
        AssertRejected([0xFF, 0xD8, 0xFF, 0xD9]);
    }

    [Test]
    [Arguments((byte)0xC3)]
    [Arguments((byte)0xC9)]
    public void TryRead_LosslessOrArithmeticFrame_IsRejected(byte frameMarker)
    {
        AssertRejected(Jpeg(frameMarker, width: 64, height: 64, components: 3));
    }

    [Test]
    public void TryRead_ZeroWidth_IsRejected()
    {
        AssertRejected(Jpeg(0xC0, width: 0, height: 64, components: 3));
    }

    private static void AssertRejected(byte[] bytes)
    {
        AssertEx.False(JpegImageDimensions.TryRead(bytes, out var width, out var height, out var components));
        AssertEx.Equal(expected: 0, width + height + components);
    }

    private static byte[] Jpeg(byte frameMarker, int width, int height, byte components)
    {
        var frameLength = 8 + (3 * components);
        var frame = new List<byte>
        {
            0xFF, frameMarker, (byte)(frameLength >> 8), (byte)frameLength, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, components
        };
        for (var component = 1; component <= components; component++)
        {
            frame.AddRange([(byte)component, 0x11, 0x00]);
        }

        // SOI, then a JFIF APP0 segment (length 16) the walker must skip, then the frame, a scan and EOI.
        byte[] app0 = [0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
        return [0xFF, 0xD8, .. app0, .. frame, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];
    }
}
