namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Buffers.Binary;

/// <summary>
///     Reads the pixel dimensions out of a PNG's IHDR header. stable-diffusion.cpp silently rounds a requested latent
///     grid up to a multiple of 64, so the produced image is frequently NOT the size that was asked for (a requested
///     100x512 comes back 128x512). The runtime must therefore report the dimensions of the bytes it actually received,
///     never the ones the caller requested — otherwise the app states a false fact about its own output.
///     <para>
///         Header-only, allocation-free, and total: the fixed PNG layout puts width/height at byte offsets 16 and 20 as
///         big-endian uint32s, so no image-decoding dependency is needed. A payload that is not a well-formed PNG (or a
///         future non-PNG format) yields <see langword="null" /> and the caller falls back.
///     </para>
/// </summary>
internal static class PngImageDimensions
{
    // The 8-byte PNG signature every PNG stream starts with.
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // "IHDR" — the first chunk's type, which the spec requires to immediately follow the signature.
    private static ReadOnlySpan<byte> IhdrChunkType => [0x49, 0x48, 0x44, 0x52];

    // Signature (8) + chunk length (4) + chunk type (4) + width (4) + height (4).
    private const int MinimumHeaderLength = 24;

    private const int ChunkTypeOffset = 12;
    private const int WidthOffset = 16;
    private const int HeightOffset = 20;

    /// <summary>
    ///     Returns the PNG's declared pixel dimensions, or <see langword="null" /> when <paramref name="imageBytes" /> is
    ///     not a PNG with a readable IHDR (too short, wrong signature, wrong first chunk, or a non-positive/oversized
    ///     dimension). Never throws.
    /// </summary>
    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> imageBytes)
    {
        if (imageBytes.Length < MinimumHeaderLength
            || !imageBytes[..Signature.Length].SequenceEqual(Signature)
            || !imageBytes.Slice(ChunkTypeOffset, IhdrChunkType.Length).SequenceEqual(IhdrChunkType))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(imageBytes.Slice(WidthOffset, sizeof(uint)));
        var height = BinaryPrimitives.ReadUInt32BigEndian(imageBytes.Slice(HeightOffset, sizeof(uint)));

        // The spec bounds both to 2^31-1 and forbids zero; anything outside that is a corrupt header, not a dimension.
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return null;
        }

        return ((int)width, (int)height);
    }
}
