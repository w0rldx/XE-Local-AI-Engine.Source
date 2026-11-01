namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Buffers.Binary;
using System.Text;

/// <summary>
///     Builds canned little-endian GGUF v3 header bytes for offline header-read tests (magic, version, tensor/kv counts,
///     then typed metadata KV pairs, then tensor infos). Mirrors the ggml GGUF spec the production readers parse.
/// </summary>
internal sealed class GgufHeaderBytesBuilder
{
    private const uint GgufMagic = 0x4655_4747; // "GGUF" little-endian.
    private const uint TypeUint32 = 4;
    private const uint TypeString = 8;
    private const uint TypeArray = 9;
    private const uint TypeUint64 = 10;

    private readonly List<(string Key, uint Type, object Value)> _kv = [];
    private readonly List<TensorSpec> _tensors = [];
    private ulong? _declaredTensorCount;

    /// <summary>Appends one tensor-info record (name, dimensions, ggml type id, data offset) after the KV section.</summary>
    public GgufHeaderBytesBuilder WithTensor(string name, ulong[] dimensions, uint ggmlType, ulong offset)
    {
        _tensors.Add(new TensorSpec(name, dimensions, ggmlType, offset));
        return this;
    }

    /// <summary>Writes this tensor_count instead of the number of appended tensors.</summary>
    public GgufHeaderBytesBuilder WithDeclaredTensorCount(ulong count)
    {
        _declaredTensorCount = count;
        return this;
    }

    public GgufHeaderBytesBuilder WithStringArray(string key, string[] values)
    {
        _kv.Add((key, TypeArray, values));
        return this;
    }

    public GgufHeaderBytesBuilder WithString(string key, string value)
    {
        _kv.Add((key, TypeString, value));
        return this;
    }

    public GgufHeaderBytesBuilder WithUint32(string key, uint value)
    {
        _kv.Add((key, TypeUint32, value));
        return this;
    }

    public GgufHeaderBytesBuilder WithUint64(string key, ulong value)
    {
        _kv.Add((key, TypeUint64, value));
        return this;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        WriteUint32(stream, GgufMagic);
        WriteUint32(stream, value: 3); // version
        WriteUint64(stream, _declaredTensorCount ?? (ulong)_tensors.Count); // tensor_count
        WriteUint64(stream, (ulong)_kv.Count);

        foreach (var (key, type, value) in _kv)
        {
            WriteGgufString(stream, key);
            WriteUint32(stream, type);
            switch (type)
            {
                case TypeString:
                    WriteGgufString(stream, (string)value);
                    break;
                case TypeUint32:
                    WriteUint32(stream, (uint)value);
                    break;
                case TypeUint64:
                    WriteUint64(stream, (ulong)value);
                    break;
                case TypeArray:
                    WriteStringArray(stream, (string[])value);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported test KV type {type}.");
            }
        }

        foreach (var tensor in _tensors)
        {
            WriteGgufString(stream, tensor.Name);
            WriteUint32(stream, (uint)tensor.Dimensions.Length);
            foreach (var dimension in tensor.Dimensions)
            {
                WriteUint64(stream, dimension);
            }

            WriteUint32(stream, tensor.GgmlType);
            WriteUint64(stream, tensor.Offset);
        }

        return stream.ToArray();
    }

    private readonly record struct TensorSpec(string Name, ulong[] Dimensions, uint GgmlType, ulong Offset);

    private static void WriteUint32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUint64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteGgufString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUint64(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    // GGUF array layout the reader parses: uint32 elementType, uint64 length, then each element (a GGUF string here).
    private static void WriteStringArray(Stream stream, string[] values)
    {
        WriteUint32(stream, TypeString);
        WriteUint64(stream, (ulong)values.Length);
        foreach (var value in values)
        {
            WriteGgufString(stream, value);
        }
    }
}
