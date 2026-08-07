namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Compiles a custom tool's declared parameters into the JSON schema the model is offered.
///     <para>
///         GBNF-safe by construction: the emitted schema carries ONLY <c>type</c>, <c>description</c>, <c>properties</c>,
///         <c>required</c> and <c>additionalProperties</c> — never <c>maxLength</c>/<c>minLength</c>/<c>maximum</c>/
///         <c>minimum</c>/<c>pattern</c>/<c>enum</c>/<c>format</c> or any other length/range/format bound. llama.cpp
///         compiles the offered schema into a GBNF grammar and length/range bounds break its sampler initialization
///         (see the <c>llamacpp-gbnf-tool-schema-bound</c> memory), so those keywords must never reach the wire. The
///         schema is also flat — parameters are scalars — so nesting depth is a constant 2 and cannot inflate the grammar.
///     </para>
///     <para>A Fixed tool takes no model input, so it compiles to a closed empty-object schema.</para>
/// </summary>
internal static class CustomToolSchemaCompiler
{
    /// <summary>The banned JSON-schema keywords, exposed so a test can assert the compiler never emits one.</summary>
    public static readonly IReadOnlyList<string> BannedSchemaKeywords =
    [
        "maxLength", "minLength", "maximum", "minimum", "exclusiveMaximum", "exclusiveMinimum",
        "pattern", "enum", "format", "multipleOf", "maxItems", "minItems", "maxProperties", "minProperties"
    ];

    /// <summary>
    ///     Compiles <paramref name="parameters" /> (ignored for <see cref="CustomToolMode.Fixed" />) into a JSON-schema
    ///     string suitable for a tool's <c>ParameterSchema</c>. Unknown declared types fall back to <c>string</c> so the
    ///     grammar stays valid; the CRUD validator (P3) is where an unknown type is rejected at author time.
    /// </summary>
    public static string Compile(CustomToolMode mode, IReadOnlyList<CustomToolParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            if (mode == CustomToolMode.Parameterized)
            {
                foreach (var parameter in parameters)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Name))
                    {
                        continue;
                    }

                    writer.WriteStartObject(parameter.Name);
                    writer.WriteString("type", MapType(parameter.Type));
                    if (!string.IsNullOrWhiteSpace(parameter.Description))
                    {
                        writer.WriteString("description", parameter.Description);
                    }

                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();

            if (mode == CustomToolMode.Parameterized)
            {
                var required = parameters
                               .Where(static parameter => parameter.Required && !string.IsNullOrWhiteSpace(parameter.Name))
                               .Select(static parameter => parameter.Name)
                               .ToList();
                if (required.Count > 0)
                {
                    writer.WriteStartArray("required");
                    foreach (var name in required)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                }
            }

            // Closed object: the model may fill only the declared inputs. Strict rejection of unknown keys is safe here
            // because the schema fully enumerates the tool's inputs (the arg-repair wrapper rejects extras).
            writer.WriteBoolean("additionalProperties", value: false);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MapType(string declaredType)
    {
        var trimmed = declaredType.Trim();
        if (string.Equals(trimmed, "number", StringComparison.OrdinalIgnoreCase))
        {
            return "number";
        }

        if (string.Equals(trimmed, "integer", StringComparison.OrdinalIgnoreCase))
        {
            return "integer";
        }

        return string.Equals(trimmed, "boolean", StringComparison.OrdinalIgnoreCase) ? "boolean" : "string";
    }
}
