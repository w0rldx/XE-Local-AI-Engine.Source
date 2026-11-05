namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

internal static class GgufStrictHeaderParser
{
    private const long MaximumHeaderBytes = 64L * 1024 * 1024;

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
        var reader = new GgufSpanReader(bytes);
        if (!reader.TryReadUInt32(out var magic) || magic != GgufHeaderReader.GgufMagic
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
                || !reader.TryReadValue(type, out var value))
            {
                return new StrictHeader
                {
                    Version = version,
                    Values = values,
                    IsComplete = false
                };
            }

            // An array or a float is consumed but not kept, so it leaves an earlier value of the same key in place.
            switch (value)
            {
                case null or double:
                    break;
                case ulong unsigned:
                    values[key] = unsigned <= long.MaxValue ? (long)unsigned : long.MaxValue;
                    break;
                default:
                    values[key] = value;
                    break;
            }
        }

        return new StrictHeader
        {
            Version = version,
            Values = values,
            IsComplete = true
        };
    }

    // A name the rest of the app cannot parse counts as undetected: the import refuses it instead of storing that label.
    public static string? ResolveQuantization(StrictHeader header)
    {
        return header.TryGetInt64("general.file_type", out var fileType)
               && GgufQuantDetector.FileTypeNames.TryGetValue(fileType, out var quant)
               && GgufQuantDetector.IsCanonical(quant)
            ? quant
            : null;
    }

    internal sealed class StrictHeader
    {
        public required uint? Version { get; init; }

        public required IReadOnlyDictionary<string, object> Values { get; init; }

        public required bool IsComplete { get; init; }

        public static StrictHeader Invalid { get; } = new()
        {
            Version = null,
            Values = new Dictionary<string, object>(StringComparer.Ordinal),
            IsComplete = false
        };

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
}
