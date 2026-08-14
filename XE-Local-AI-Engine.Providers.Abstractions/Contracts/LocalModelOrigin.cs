namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Typed provenance for a locally acquired model.</summary>
[JsonConverter(typeof(LocalModelOriginJsonConverter))]
public enum LocalModelOrigin
{
    /// <summary>Acquired from a pinned Hugging Face repository revision.</summary>
    HuggingFace = 0,

    /// <summary>Copied from an operator-selected local GGUF file.</summary>
    Imported = 1
}

/// <summary>Strict lowercase JSON representation for <see cref="LocalModelOrigin" />.</summary>
public sealed class LocalModelOriginJsonConverter : JsonConverter<LocalModelOrigin>
{
    /// <inheritdoc />
    public override LocalModelOrigin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A local model origin must be a string.");
        }

        return reader.GetString() switch
        {
            "huggingface" => LocalModelOrigin.HuggingFace,
            "imported" => LocalModelOrigin.Imported,
            _ => throw new JsonException("The local model origin is not supported.")
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LocalModelOrigin value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            LocalModelOrigin.HuggingFace => "huggingface",
            LocalModelOrigin.Imported => "imported",
            _ => throw new JsonException("The local model origin is not supported.")
        });
    }
}
