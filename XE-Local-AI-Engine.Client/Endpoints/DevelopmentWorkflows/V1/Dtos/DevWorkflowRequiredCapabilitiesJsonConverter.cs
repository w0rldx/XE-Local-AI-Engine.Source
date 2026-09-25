namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Reads a node's <c>requiredCapabilities</c> leniently, so a malformed one reaches the runtime's parser and is refused
///     there with its own sentence instead of the serializer's CLR type name.
/// </summary>
/// <remarks>
///     A string-to-string object reads as usual. Anything else, such as an array or a non-string reason, is kept verbatim
///     and written back verbatim into the graph document the parser validates, which names the node and the expected
///     keys. The wire type stays the dictionary, so the contract is unchanged.
/// </remarks>
public sealed class DevWorkflowRequiredCapabilitiesJsonConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public override IReadOnlyDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind == JsonValueKind.Object && element.EnumerateObject().All(static property => property.Value.ValueKind == JsonValueKind.String))
        {
            return element.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.GetString()!, StringComparer.Ordinal);
        }

        return new Unparsed(element.Clone());
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value is Unparsed unparsed)
        {
            unparsed.Raw.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var (key, reason) in value)
        {
            writer.WriteString(key, reason);
        }

        writer.WriteEndObject();
    }

    /// <summary>The malformed value, carried as an empty dictionary until the parser refuses it.</summary>
    [SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "A private carrier, never named on the wire.")]
    private sealed class Unparsed : IReadOnlyDictionary<string, string>
    {
        public Unparsed(JsonElement raw)
        {
            Raw = raw;
        }

        public JsonElement Raw { get; }

        public int Count => 0;

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public string this[string key] => throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
