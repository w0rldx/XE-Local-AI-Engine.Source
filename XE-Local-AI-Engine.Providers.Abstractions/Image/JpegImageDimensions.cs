namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

using System.Buffers.Binary;

/// <summary>
///     Reads the pixel dimensions and component count out of a JPEG's start-of-frame header.
/// </summary>
/// <remarks>
///     Header-only and allocation-free: walks the marker segments after SOI until the first SOF0/SOF1/SOF2 (baseline, extended
///     sequential, progressive — the Huffman frames stable-diffusion.cpp's decoder reads) and stops there. Any other frame type
///     (lossless, arithmetic), a scan or end-of-image before a frame, or a truncated segment is a rejection, never an exception.
///     The component count lets a caller refuse CMYK (4) input the runtime cannot condition on.
/// </remarks>
public static class JpegImageDimensions
{
    private const byte MarkerPrefix = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte Temporary = 0x01;
    private const byte FirstRestart = 0xD0;
    private const byte LastRestart = 0xD7;
    private const byte FirstFrame = 0xC0;
    private const byte LastFrame = 0xCF;
    private const byte HuffmanTable = 0xC4;
    private const byte ArithmeticConditioning = 0xCC;
    private const byte JpegExtension = 0xC8;
    private const byte ProgressiveFrame = 0xC2;
    private const byte StuffedZero = 0x00;

    // Segment length (2) + sample precision (1) + height (2) + width (2) + component count (1).
    private const int FrameHeaderLength = 8;

    /// <summary>
    ///     Returns <see langword="true" /> with the frame's declared <paramref name="width" />, <paramref name="height" /> and
    ///     <paramref name="components" /> when <paramref name="bytes" /> is a JPEG whose first frame header is SOF0, SOF1 or SOF2
    ///     and declares non-zero dimensions; otherwise <see langword="false" /> with all three outputs zero. Never throws.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out int width, out int height, out int components)
    {
        width = 0;
        height = 0;
        components = 0;

        if (bytes.Length < 2 || bytes[0] != MarkerPrefix || bytes[1] != StartOfImage)
        {
            return false;
        }

        var position = 2;
        while (position < bytes.Length)
        {
            if (bytes[position] != MarkerPrefix)
            {
                return false;
            }

            // Any number of 0xFF fill bytes may precede a marker code.
            while (position < bytes.Length && bytes[position] == MarkerPrefix)
            {
                position++;
            }

            if (position >= bytes.Length)
            {
                return false;
            }

            var marker = bytes[position++];
            if (marker is Temporary or (>= FirstRestart and <= LastRestart))
            {
                continue; // Standalone markers carry no length.
            }

            if (marker is EndOfImage or StartOfScan or StartOfImage or StuffedZero)
            {
                return false; // No frame header before the image data (or a stray marker).
            }

            if (position + 2 > bytes.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > bytes.Length)
            {
                return false;
            }

            if (marker is >= FirstFrame and <= LastFrame and not HuffmanTable and not JpegExtension and not ArithmeticConditioning)
            {
                // The first frame header decides: only the Huffman sequential/progressive frames are accepted.
                if (marker > ProgressiveFrame || segmentLength < FrameHeaderLength)
                {
                    return false;
                }

                var frameHeight = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 3, 2));
                var frameWidth = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 5, 2));
                var frameComponents = bytes[position + 7];
                if (frameHeight == 0 || frameWidth == 0 || frameComponents == 0)
                {
                    return false;
                }

                width = frameWidth;
                height = frameHeight;
                components = frameComponents;
                return true;
            }

            position += segmentLength;
        }

        return false;
    }
}
