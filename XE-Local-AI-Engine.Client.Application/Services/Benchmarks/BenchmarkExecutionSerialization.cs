namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;

/// <summary>
///     The ONLY serializer for the benchmark run's stored <c>output_parts_json</c> blob (the judge's own blobs ride
///     <c>BenchmarkJudgeSerialization</c>).
/// </summary>
/// <remarks>
///     Public because a reader must never re-derive the options at the call site: <see cref="JsonSerializerDefaults.Web" />
///     is camelCase, so deserializing with default options binds every property to its default and hands the API a
///     zeroed payload instead of failing.
/// </remarks>
public static class BenchmarkExecutionSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeParts(IEnumerable<BenchmarkOutputPart> parts) =>
        JsonSerializer.SerializeToUtf8Bytes(parts, JsonOptions);

    public static IReadOnlyList<BenchmarkOutputPart> DeserializeParts(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<BenchmarkOutputPart[]>(payload, JsonOptions)
        ?? throw new BenchmarkSnapshotException("Benchmark output parts are invalid.");
}
