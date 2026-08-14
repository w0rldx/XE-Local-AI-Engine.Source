namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Buffers.Binary;
using System.Text;

internal static class GgufStrictHeaderParser
{
    private const uint Magic = 0x4655_4747;
    private const long MaximumHeaderBytes = 64L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<long, string> Quantizations = new Dictionary<long, string>
    {
        [0] = "F32",
        [1] = "F16",
        [2] = "Q4_0",
        [3] = "Q4_1",
        [6] = "Q5_0",
        [7] = "Q5_1",
        [8] = "Q8_0",
        [10] = "Q2_K",
        [11] = "Q3_K_S",
        [12] = "Q3_K_M",
        [13] = "Q3_K_L",
        [14] = "Q4_K_S",
        [15] = "Q4_K_M",
        [16] = "Q5_K_S",
        [17] = "Q5_K_M",
        [18] = "Q6_K",
        [19] = "IQ2_XXS",
        [20] = "IQ2_XS",
        [21] = "IQ3_XXS",
        [22] = "IQ1_S",
        [23] = "IQ4_NL",
        [24] = "IQ3_S",
        [25] = "IQ2_S",
        [26] = "IQ4_XS",
        [27] = "I8",
        [28] = "I16",
        [29] = "I32",
        [30] = "I64",
        [31] = "F64",
        [32] = "IQ1_M",
        [33] = "BF16"
    };

    public static async Task<StrictHeader> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<StrictHeader> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            return StrictHeader.Invalid;
        }

        stream.Position = 0;
        var length = Math.Min(stream.Length, MaximumHeaderBytes);
        if (length < 24)
        {
            return StrictHeader.Invalid;
        }

        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Parse(bytes);
    }

    private static StrictHeader Parse(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes);
        if (!reader.TryReadUInt32(out var magic) || magic != Magic
                                                 || !reader.TryReadUInt32(out var version)
                                                 || !reader.TryReadUInt64(out _)
                                                 || !reader.TryReadUInt64(out var count))
        {
            return StrictHeader.Invalid;
        }

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong index = 0; index < count; index++)
        {
            if (!reader.TryReadString(out var key)
                || !reader.TryReadUInt32(out var type)
                || !TryReadValue(ref reader, type, out var value))
            {
                return new StrictHeader(version, values, IsComplete: false);
            }

            if (value is not null)
            {
                values[key] = value;
            }
        }

        return new StrictHeader(version, values, IsComplete: true);
    }

    public static string? ResolveQuantization(StrictHeader header)
    {
        return header.TryGetInt64("general.file_type", out var fileType) && Quantizations.TryGetValue(fileType, out var quant)
            ? quant
            : null;
    }

    private static bool TryReadValue(ref Reader reader, uint type, out object? value)
    {
        value = null;
        switch (type)
        {
            case 0:
            case 1:
            case 7:
                return reader.TryReadByte(out var byteValue) && Assign((long)byteValue, out value);
            case 2:
                return reader.TryReadUInt16(out var uint16) && Assign((long)uint16, out value);
            case 3:
                return reader.TryReadInt16(out var int16) && Assign((long)int16, out value);
            case 4:
                return reader.TryReadUInt32(out var uint32) && Assign((long)uint32, out value);
            case 5:
                return reader.TryReadInt32(out var int32) && Assign((long)int32, out value);
            case 6:
                return reader.TrySkip(4);
            case 8:
                return reader.TryReadString(out var stringValue) && Assign(stringValue, out value);
            case 9:
                return TrySkipArray(ref reader);
            case 10:
                if (!reader.TryReadUInt64(out var uint64))
                {
                    return false;
                }

                value = uint64 <= long.MaxValue ? (long)uint64 : long.MaxValue;
                return true;
            case 11:
                return reader.TryReadInt64(out var int64) && Assign(int64, out value);
            case 12:
                return reader.TrySkip(8);
            default:
                return false;
        }
    }

    private static bool TrySkipArray(ref Reader reader)
    {
        if (!reader.TryReadUInt32(out var elementType) || !reader.TryReadUInt64(out var length))
        {
            return false;
        }

        for (ulong index = 0; index < length; index++)
        {
            if (!TryReadValue(ref reader, elementType, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Assign<T>(T source, out object? target)
    {
        target = source;
        return true;
    }

    internal sealed record StrictHeader(uint? Version, IReadOnlyDictionary<string, object> Values, bool IsComplete)
    {
        public static StrictHeader Invalid { get; } = new(null,
            new Dictionary<string, object>(StringComparer.Ordinal),
            IsComplete: false);

        public string? GetString(string key) =>
            Values.TryGetValue(key, out var value) ? value as string : null;

        public bool TryGetInt64(string key, out long value)
        {
            if (Values.TryGetValue(key, out var found) && found is long number)
            {
                value = number;
                return true;
            }

            value = 0;
            return false;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public bool TrySkip(int count) =>
            TryTake(count, out _);

        public bool TryReadByte(out byte value)
        {
            if (TryTake(1, out var s))
            {
                value = s[0];
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (TryTake(2, out var s))
            {
                value = BinaryPrimitives.ReadUInt16LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt16(out short value)
        {
            if (TryTake(2, out var s))
            {
                value = BinaryPrimitives.ReadInt16LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (TryTake(4, out var s))
            {
                value = BinaryPrimitives.ReadUInt32LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt32(out int value)
        {
            if (TryTake(4, out var s))
            {
                value = BinaryPrimitives.ReadInt32LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt64(out ulong value)
        {
            if (TryTake(8, out var s))
            {
                value = BinaryPrimitives.ReadUInt64LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt64(out long value)
        {
            if (TryTake(8, out var s))
            {
                value = BinaryPrimitives.ReadInt64LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadUInt64(out var length) || length > int.MaxValue || !TryTake((int)length, out var slice))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(slice);
            return true;
        }

        private bool TryTake(int count, out ReadOnlySpan<byte> slice)
        {
            if (count < 0 || _position > _bytes.Length - count)
            {
                slice = default;
                return false;
            }

            slice = _bytes.Slice(_position, count);
            _position += count;
            return true;
        }
    }
}
