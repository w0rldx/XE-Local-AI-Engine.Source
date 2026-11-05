namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Buffers.Binary;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Stream-parses a local GGUF file's header, key-value section and tensor-info block without reading tensor data.</summary>
/// <remarks>
///     The file is untrusted: every length is checked against the bytes left in the file, arrays are skipped by seeking,
///     and only a bounded buffer is held. A short read (<see cref="EndOfStreamException" />) or a broken bound
///     (<see cref="InvalidDataException" />) is caught at the top and ends the read with a typed <see cref="GgufTensorReadFailure" />.
/// </remarks>
internal static class GgufTensorBlockReader
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxDimensions = 8;
    private const long MaxTensorCount = 65_536;
    private const long MaxKeyValueCount = 1_048_576;
    private const int MaxKeyBytes = 64 * 1024;

    // ggml's GGML_MAX_NAME is 64; the slack tolerates a converter that writes a longer name without letting one grow unbounded.
    private const int MaxTensorNameBytes = 256;

    // Kept string values (chat templates) above this are skipped rather than buffered.
    private const int MaxKeptStringBytes = BufferSize;

    private const long MinimumTensorInfoBytes = 8 + 1 + 4 + 8 + 4 + 8;

    public static async Task<GgufTensorInspection> ReadFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                return await ReadAsync(stream, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Failed(GgufTensorReadFailure.Unreadable, version: null, values: null, fileSize: 0);
        }
    }

    /// <summary>Parses <paramref name="stream" /> from its start; it must be readable and seekable.</summary>
    public static async Task<GgufTensorInspection> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            return Failed(GgufTensorReadFailure.Unreadable, version: null, values: null, fileSize: 0);
        }

        var cursor = new Cursor(stream);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        uint? version = null;
        try
        {
            if (await cursor.ReadUInt32Async(ct).ConfigureAwait(false) != GgufHeaderReader.GgufMagic)
            {
                return Failed(GgufTensorReadFailure.Unreadable, version, values: null, cursor.Length);
            }

            version = await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
            if (version is not (2 or 3))
            {
                return Failed(GgufTensorReadFailure.UnsupportedVersion, version, values: null, cursor.Length);
            }

            var tensorCount = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
            var kvCount = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
            if (tensorCount > MaxTensorCount || kvCount > MaxKeyValueCount)
            {
                return Failed(GgufTensorReadFailure.Malformed, version, values, cursor.Length);
            }

            // A key-value pair is at least an 8-byte key length, a 4-byte type and a 1-byte value.
            if ((long)kvCount > cursor.Remaining / 13)
            {
                return Failed(GgufTensorReadFailure.Truncated, version, values, cursor.Length);
            }

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = await cursor.ReadStringAsync(MaxKeyBytes, ct).ConfigureAwait(false) ?? throw new InvalidDataException("GGUF value out of bounds.");
                var valueType = await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
                if (await ReadValueAsync(cursor, valueType, ct).ConfigureAwait(false) is { } value)
                {
                    values[key] = value;
                }
            }

            // Every tensor info costs at least MinimumTensorInfoBytes, so a count the file cannot hold is refused before the loop.
            if ((long)tensorCount > cursor.Remaining / MinimumTensorInfoBytes)
            {
                return Failed(GgufTensorReadFailure.Truncated, version, values, cursor.Length);
            }

            var tensors = new List<GgufTensorInfo>((int)tensorCount);
            for (ulong i = 0; i < tensorCount; i++)
            {
                tensors.Add(await ReadTensorInfoAsync(cursor, ct).ConfigureAwait(false));
            }

            return Build(GgufTensorReadFailure.None, version, values, (long)tensorCount, tensors, cursor.Position, cursor.Length);
        }
        catch (EndOfStreamException)
        {
            return Failed(GgufTensorReadFailure.Truncated, version, values, cursor.Length);
        }
        catch (InvalidDataException)
        {
            return Failed(GgufTensorReadFailure.Malformed, version, values, cursor.Length);
        }
    }

    private static async Task<GgufTensorInfo> ReadTensorInfoAsync(Cursor cursor, CancellationToken ct)
    {
        var name = await cursor.ReadStringAsync(MaxTensorNameBytes, ct).ConfigureAwait(false) ?? throw new InvalidDataException("GGUF value out of bounds.");
        var dimensionCount = await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
        if (dimensionCount is 0 or > MaxDimensions)
        {
            throw new InvalidDataException("GGUF value out of bounds.");
        }

        var dimensions = new long[dimensionCount];
        for (var d = 0; d < dimensions.Length; d++)
        {
            var dimension = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
            if (dimension > long.MaxValue)
            {
                throw new InvalidDataException("GGUF value out of bounds.");
            }

            dimensions[d] = (long)dimension;
        }

        var type = await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
        var offset = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
        if (offset > long.MaxValue)
        {
            throw new InvalidDataException("GGUF value out of bounds.");
        }

        var traits = GgmlTypeTraits.Find(type);
        var elements = ElementCount(dimensions);
        return new GgufTensorInfo
        {
            Name = name,
            Dimensions = dimensions,
            GgmlType = type,
            GgmlTypeName = traits?.Name,
            Offset = (long)offset,
            ElementCount = elements,
            ByteSize = traits is { } known ? ByteSize(dimensions[0], elements, known) : null
        };
    }

    // Overflow-safe: an element count or byte size beyond long is a malformed file, never a wrapped number.
    private static long ElementCount(long[] dimensions)
    {
        try
        {
            var elements = 1L;
            foreach (var dimension in dimensions)
            {
                elements = checked(elements * dimension);
            }

            return elements;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("GGUF tensor element count overflows.", ex);
        }
    }

    private static long? ByteSize(long rowLength, long elements, GgmlTypeTraits traits)
    {
        try
        {
            return rowLength % traits.BlockSize == 0 ? checked(elements / traits.BlockSize * traits.TypeSize) : null;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("GGUF tensor size overflows.", ex);
        }
    }

    // Same value typing as GgufSpanReader (long for narrow integers, ulong for UINT64, double for floats), so GgufHeaderReader.Build, the shared projection, reads the same dictionary.
    private static async Task<object?> ReadValueAsync(Cursor cursor, uint valueType, CancellationToken ct)
    {
        switch (valueType)
        {
            case 0:
            case 1:
            case 7:
                return (long)(await cursor.ReadBytesAsync(count: 1, ct).ConfigureAwait(false)).Span[0];
            case 2:
                return (long)BinaryPrimitives.ReadUInt16LittleEndian((await cursor.ReadBytesAsync(count: 2, ct).ConfigureAwait(false)).Span);
            case 3:
                return (long)BinaryPrimitives.ReadInt16LittleEndian((await cursor.ReadBytesAsync(count: 2, ct).ConfigureAwait(false)).Span);
            case 4:
                return (long)await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
            case 5:
                return (long)BinaryPrimitives.ReadInt32LittleEndian((await cursor.ReadBytesAsync(count: 4, ct).ConfigureAwait(false)).Span);
            case 6:
                return (double)BinaryPrimitives.ReadSingleLittleEndian((await cursor.ReadBytesAsync(count: 4, ct).ConfigureAwait(false)).Span);
            case 8:
                return await cursor.ReadStringAsync(MaxKeptStringBytes, ct).ConfigureAwait(false);
            case 9:
                await SkipArrayAsync(cursor, ct).ConfigureAwait(false);
                return null;
            case 10:
                return await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
            case 11:
                return BinaryPrimitives.ReadInt64LittleEndian((await cursor.ReadBytesAsync(count: 8, ct).ConfigureAwait(false)).Span);
            case 12:
                return BinaryPrimitives.ReadDoubleLittleEndian((await cursor.ReadBytesAsync(count: 8, ct).ConfigureAwait(false)).Span);
            default:
                throw new InvalidDataException("GGUF value out of bounds.");
        }
    }

    private static async Task SkipArrayAsync(Cursor cursor, CancellationToken ct)
    {
        var elementType = await cursor.ReadUInt32Async(ct).ConfigureAwait(false);
        var length = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
        long width = elementType switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            8 => 0,
            _ => throw new InvalidDataException("GGUF value out of bounds.") // nested arrays and unknown types, as llama.cpp refuses them
        };

        if (width > 0)
        {
            if (length > (ulong)(cursor.Remaining / width))
            {
                throw new EndOfStreamException();
            }

            cursor.Skip((long)length * width);
            return;
        }

        // A string element is at least its 8-byte length prefix.
        if (length > (ulong)(cursor.Remaining / 8))
        {
            throw new EndOfStreamException();
        }

        for (ulong i = 0; i < length; i++)
        {
            var size = await cursor.ReadUInt64Async(ct).ConfigureAwait(false);
            if (size > (ulong)cursor.Remaining)
            {
                throw new EndOfStreamException();
            }

            cursor.Skip((long)size);
        }
    }

    private static GgufTensorInspection Failed(GgufTensorReadFailure failure, uint? version, Dictionary<string, object>? values, long fileSize)
    {
        return Build(failure, version, values ?? new Dictionary<string, object>(StringComparer.Ordinal), tensorCount: 0, [], headerBytes: 0, fileSize);
    }

    // Exact parameter count when the converter wrote no general.parameter_count; null if the sum overflows long.
    private static long? SumElements(IReadOnlyList<GgufTensorInfo> tensors)
    {
        try
        {
            return tensors.Aggregate(seed: 0L, static (sum, tensor) => checked(sum + tensor.ElementCount));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static GgufTensorInspection Build(GgufTensorReadFailure failure,
        uint? version,
        Dictionary<string, object> values,
        long tensorCount,
        IReadOnlyList<GgufTensorInfo> tensors,
        long headerBytes,
        long fileSize)
    {
        var header = GgufHeaderReader.Build(values);
        return new GgufTensorInspection
        {
            Failure = failure,
            Version = version,
            Architecture = header.Architecture,
            ModelName = values.TryGetValue("general.name", out var name) ? name as string : null,
            ParamCount = header.ParamCount ?? (failure == GgufTensorReadFailure.None ? SumElements(tensors) : null),
            FileType = values.TryGetValue("general.file_type", out var fileType) && fileType is long number
                ? GgufQuantDetector.FileTypeNames.GetValueOrDefault(number, header.QuantType)
                : null,
            BlockCount = header.BlockCount,
            NextnPredictLayers = GgufHeaderReader.GetLongOrNull(values, $"{header.Architecture}.nextn_predict_layers"),
            Alignment = GgufHeaderReader.GetLongOrNull(values, "general.alignment"),
            AttentionHeadCount = header.AttentionHeadCount,
            AttentionHeadCountKv = header.AttentionHeadCountKV,
            EmbeddingLength = header.EmbeddingLength,
            ContextLength = header.ContextLength,
            ExpertCount = header.ExpertCount,
            ChatTemplate = header.ChatTemplate,
            SplitCount = values.TryGetValue("split.count", out var split) && split is long splitCount ? splitCount : null,
            TensorCount = tensorCount,
            Tensors = tensors,
            HeaderBytes = headerBytes,
            FileSizeBytes = fileSize
        };
    }

    /// <summary>A forward-only buffered reader that seeks over skipped spans and fails short reads as truncation.</summary>
    private sealed class Cursor
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[BufferSize];
        private long _bufferStart;
        private int _position;
        private int _length;

        public Cursor(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
            _stream.Position = 0;
        }

        public long Length { get; }

        public long Position => _bufferStart + _position;

        public long Remaining => Length - Position;

        public async Task<uint> ReadUInt32Async(CancellationToken ct)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian((await ReadBytesAsync(count: 4, ct).ConfigureAwait(false)).Span);
        }

        public async Task<ulong> ReadUInt64Async(CancellationToken ct)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian((await ReadBytesAsync(count: 8, ct).ConfigureAwait(false)).Span);
        }

        /// <summary>Reads a GGUF string, returning <see langword="null" /> (bytes skipped) when it exceeds <paramref name="maxBytes" />.</summary>
        public async Task<string?> ReadStringAsync(int maxBytes, CancellationToken ct)
        {
            var size = await ReadUInt64Async(ct).ConfigureAwait(false);
            if (size > (ulong)Remaining)
            {
                throw new EndOfStreamException();
            }

            if (size > (ulong)maxBytes)
            {
                Skip((long)size);
                return null;
            }

            return Encoding.UTF8.GetString((await ReadBytesAsync((int)size, ct).ConfigureAwait(false)).Span);
        }

        /// <summary>Returns the next <paramref name="count" /> bytes (at most the buffer size); valid until the next read.</summary>
        public async Task<ReadOnlyMemory<byte>> ReadBytesAsync(int count, CancellationToken ct)
        {
            if (_length - _position < count)
            {
                await FillAsync(count, ct).ConfigureAwait(false);
            }

            var slice = new ReadOnlyMemory<byte>(_buffer, _position, count);
            _position += count;
            return slice;
        }

        public void Skip(long count)
        {
            if (count > Remaining)
            {
                throw new EndOfStreamException();
            }

            if (count <= _length - _position)
            {
                _position += (int)count;
                return;
            }

            _bufferStart = Position + count;
            _position = 0;
            _length = 0;
            _stream.Position = _bufferStart;
        }

        private async Task FillAsync(int count, CancellationToken ct)
        {
            var kept = _length - _position;
            Buffer.BlockCopy(_buffer, _position, _buffer, dstOffset: 0, kept);
            _bufferStart += _position;
            _position = 0;
            _length = kept;
            _stream.Position = _bufferStart + _length;
            while (_length < count)
            {
                var read = await _stream.ReadAsync(_buffer.AsMemory(_length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                _length += read;
            }
        }
    }
}
