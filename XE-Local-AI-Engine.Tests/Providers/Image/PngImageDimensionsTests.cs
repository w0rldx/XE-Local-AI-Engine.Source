namespace XE_Local_AI_Engine.Tests.Providers.Image;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class PngImageDimensionsTests
{
    // Signature + IHDR length (13) + "IHDR" + width 640 + height 480.
    private static readonly byte[] Header =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x02, 0x80, 0x00, 0x00, 0x01, 0xE0
    ];

    [Test]
    public void TryRead_ValidHeader_ReturnsDeclaredDimensions()
    {
        var dimensions = PngImageDimensions.TryRead(Header);

        AssertEx.Equal(new ImageDimensions(Width: 640, Height: 480), dimensions);
    }

    [Test]
    public void TryRead_TruncatedHeader_ReturnsNull()
    {
        AssertEx.Null(PngImageDimensions.TryRead(Header.AsSpan(0, Header.Length - 1)));
    }

    [Test]
    public void TryRead_JpegBytes_ReturnsNull()
    {
        byte[] jpegStart = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[20]];

        AssertEx.Null(PngImageDimensions.TryRead(jpegStart));
    }
}
