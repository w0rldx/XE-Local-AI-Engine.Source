namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Buffers.Binary;
using System.Text;

/// <summary>Forward-only little-endian reader over untrusted GGUF bytes. Running out of bytes or an unreadable value returns <see langword="false" />; nothing throws.</summary>
internal ref struct GgufSpanReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _position;

    public GgufSpanReader(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (TryTake(count: 4, out var slice))
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(slice);
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryReadUInt64(out ulong value)
    {
        if (TryTake(count: 8, out var slice))
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(slice);
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

    /// <summary>Reads one value of GGUF type <paramref name="type" /> as <c>long</c> (narrow integers and INT64), <c>ulong</c> (UINT64), <c>double</c> (floats) or <c>string</c>.</summary>
    /// <remarks>
    ///     An array is consumed to stay aligned and yields <see langword="null" />. An unknown type has no known width and
    ///     a nested array is refused, so both return <see langword="false" />.
    /// </remarks>
    public bool TryReadValue(uint type, out object? value)
    {
        value = null;
        ReadOnlySpan<byte> slice;
        switch (type)
        {
            case 0: // UINT8
            case 1: // INT8
            case 7: // BOOL
                return TryTake(count: 1, out slice) && Assign((long)slice[0], out value);
            case 2: // UINT16
                return TryTake(count: 2, out slice) && Assign((long)BinaryPrimitives.ReadUInt16LittleEndian(slice), out value);
            case 3: // INT16
                return TryTake(count: 2, out slice) && Assign((long)BinaryPrimitives.ReadInt16LittleEndian(slice), out value);
            case 4: // UINT32
                return TryTake(count: 4, out slice) && Assign((long)BinaryPrimitives.ReadUInt32LittleEndian(slice), out value);
            case 5: // INT32
                return TryTake(count: 4, out slice) && Assign((long)BinaryPrimitives.ReadInt32LittleEndian(slice), out value);
            case 6: // FLOAT32
                return TryTake(count: 4, out slice) && Assign((double)BinaryPrimitives.ReadSingleLittleEndian(slice), out value);
            case 8: // STRING
                return TryReadString(out var text) && Assign(text, out value);
            case 9: // ARRAY
                return TrySkipArray();
            case 10: // UINT64
                return TryTake(count: 8, out slice) && Assign(BinaryPrimitives.ReadUInt64LittleEndian(slice), out value);
            case 11: // INT64
                return TryTake(count: 8, out slice) && Assign(BinaryPrimitives.ReadInt64LittleEndian(slice), out value);
            case 12: // FLOAT64
                return TryTake(count: 8, out slice) && Assign(BinaryPrimitives.ReadDoubleLittleEndian(slice), out value);
            default:
                return false;
        }
    }

    private bool TrySkipArray()
    {
        if (!TryReadUInt32(out var elementType) || !TryReadUInt64(out var length))
        {
            return false;
        }

        // Recursing to a depth the file chooses would end in a stack overflow, which exits the process past any catch. llama.cpp refuses nested arrays too, so no loadable model holds one.
        if (elementType == 9)
        {
            return false;
        }

        for (ulong index = 0; index < length; index++)
        {
            if (!TryReadValue(elementType, out _))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryTake(int count, out ReadOnlySpan<byte> slice)
    {
        // Compared this way round so that a count near int.MaxValue cannot wrap past the check.
        if (count < 0 || _position > _bytes.Length - count)
        {
            slice = default;
            return false;
        }

        slice = _bytes.Slice(_position, count);
        _position += count;
        return true;
    }

    private static bool Assign(object source, out object? target)
    {
        target = source;
        return true;
    }
}
