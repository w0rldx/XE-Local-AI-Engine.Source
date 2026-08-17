namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Canonical JSON for the benchmark launch evidence — the persisted launch receipt and the runtime environment
///     facts — plus the SHA-256 over it that makes two runs' evidence comparable by a single value.
/// </summary>
/// <remarks>
///     Canonical means: object members emitted in ordinal name order (so a later reordering of a record's properties
///     cannot change a stored hash), nothing omitted (<see cref="JsonIgnoreCondition.Never" />, so a member that turns
///     <see langword="null" /> is a visible difference rather than an absence), no indentation, invariant formatting
///     throughout, and enums written as camel-case NAMES — an ordinal would silently re-label every stored receipt the
///     day a member is inserted, and it reaches the UI as a number nobody can read.
/// </remarks>
public static class BenchmarkCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    /// <summary>The canonical JSON text for <paramref name="value" />.</summary>
    public static string Serialize<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, SerializerOptions);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false
               }))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The lowercase-hex SHA-256 of an already-canonical JSON document.</summary>
    public static string Hash(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    /// <summary>The lowercase-hex SHA-256 of <paramref name="value" />'s canonical JSON.</summary>
    public static string HashOf<T>(T value) =>
        Hash(Serialize(value));

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static member => member.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                return;

            default:
                element.WriteTo(writer);
                return;
        }
    }
}
