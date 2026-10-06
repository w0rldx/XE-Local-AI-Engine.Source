namespace XE_Local_AI_Engine.Client.Common;

using System.Buffers;
using System.Text.Json;

/// <summary>
///     Runs a synchronous <see cref="Utf8JsonWriter" /> over an in-memory sink and returns or keeps the written bytes.
/// </summary>
public static class Utf8JsonBuffer
{
    public static byte[] Write(Action<Utf8JsonWriter> write, JsonWriterOptions options = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, write, options);
        return buffer.WrittenSpan.ToArray();
    }

    public static void Write(IBufferWriter<byte> sink, Action<Utf8JsonWriter> write, JsonWriterOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(write);

#pragma warning disable MA0045 // In-memory sink: no I/O to await; callers are synchronous canonical-bytes functions.
        using var writer = new Utf8JsonWriter(sink, options);
#pragma warning restore MA0045
        write(writer);
    }
}
