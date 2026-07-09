namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Globalization;
using System.Text.Json;

/// <summary>Outcome of validating (and coercing) a model's tool arguments against the tool's JSON schema.</summary>
internal readonly record struct ToolArgumentValidation(bool IsValid, string? Reason)
{
    public static ToolArgumentValidation Valid { get; } = new(true, null);

    public static ToolArgumentValidation Invalid(string reason)
    {
        return new ToolArgumentValidation(false, reason);
    }
}

/// <summary>
///     Applies tolerant coercion and then structural validation to the arguments a model supplied for a tool call,
///     using the tool's own model-visible JSON schema. Coercion first fixes the mistakes small local models make most
///     often — a number sent as a string, a boolean sent as <c>"true"</c>/<c>"false"</c>, or a lone value where an array
///     is expected — by rewriting the argument in place so a well-intentioned call is not bounced on a formatting nit.
///     Validation then checks the request is answerable at all: required properties present, declared property types
///     roughly matched, and no unknown properties the schema does not describe. The check is deliberately permissive
///     where the schema is loose (no declared properties, or <c>additionalProperties</c> allowed) so it never rejects a
///     call a correct handler would have accepted.
/// </summary>
internal static class ToolArgumentValidator
{
    /// <summary>
    ///     Coerces common small-model argument mistakes in <paramref name="arguments" /> in place, then validates the
    ///     (post-coercion) arguments against <paramref name="schema" />. Returns the first structural problem found, or
    ///     <see cref="ToolArgumentValidation.Valid" /> when the arguments are acceptable.
    /// </summary>
    public static ToolArgumentValidation CoerceAndValidate(JsonElement schema, IDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // A non-object schema (or one with no property map) carries nothing to validate against; accept as-is rather
        // than inventing constraints the tool never declared.
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return ToolArgumentValidation.Valid;
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;

        if (hasProperties)
        {
            Coerce(properties, arguments);
        }

        if (TryFindRequiredViolation(schema, arguments, out var requiredReason))
        {
            return ToolArgumentValidation.Invalid(requiredReason);
        }

        if (!hasProperties)
        {
            // Nothing more to check without a declared property map.
            return ToolArgumentValidation.Valid;
        }

        if (TryFindTypeViolation(properties, arguments, out var typeReason))
        {
            return ToolArgumentValidation.Invalid(typeReason);
        }

        if (TryFindUnknownPropertyViolation(schema, properties, arguments, out var unknownReason))
        {
            return ToolArgumentValidation.Invalid(unknownReason);
        }

        return ToolArgumentValidation.Valid;
    }

    private static void Coerce(JsonElement properties, IDictionary<string, object?> arguments)
    {
        foreach (var key in arguments.Keys.ToList())
        {
            if (!properties.TryGetProperty(key, out var propertySchema))
            {
                continue;
            }

            var targets = SchemaTypes(propertySchema);
            if (targets.Count == 0)
            {
                continue;
            }

            var value = arguments[key];
            var kind = KindOf(value);

            // Single value where an array is expected → wrap it as an array of one.
            if (targets.Contains("array", StringComparer.Ordinal) && kind is not JsonKind.Array and not JsonKind.Null)
            {
                arguments[key] = new List<object?> { value };
                continue;
            }

            if (kind != JsonKind.String)
            {
                continue;
            }

            var text = AsString(value);
            if (text is null)
            {
                continue;
            }

            // Boolean sent as the string "true"/"false".
            if (targets.Contains("boolean", StringComparer.Ordinal) && TryParseBoolean(text, out var parsedBool))
            {
                arguments[key] = parsedBool;
                continue;
            }

            // Number sent as a quoted string.
            var wantsInteger = targets.Contains("integer", StringComparer.Ordinal);
            var wantsNumber = wantsInteger || targets.Contains("number", StringComparer.Ordinal);
            if (wantsNumber && TryParseNumber(text, wantsInteger, out var parsedNumber))
            {
                arguments[key] = parsedNumber;
            }
        }
    }

    private static bool TryFindRequiredViolation(JsonElement schema, IDictionary<string, object?> arguments, out string reason)
    {
        reason = string.Empty;
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in required.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = entry.GetString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Present-but-null counts as missing for a required property.
            if (!arguments.TryGetValue(name, out var value) || KindOf(value) == JsonKind.Null)
            {
                reason = $"Required property '{name}' is missing.";
                return true;
            }
        }

        return false;
    }

    private static bool TryFindTypeViolation(JsonElement properties, IDictionary<string, object?> arguments, out string reason)
    {
        reason = string.Empty;
        foreach (var (key, value) in arguments)
        {
            if (!properties.TryGetProperty(key, out var propertySchema))
            {
                continue;
            }

            var targets = SchemaTypes(propertySchema);
            if (targets.Count == 0)
            {
                continue;
            }

            var kind = KindOf(value);

            // A null or an unrecognized CLR shape is not treated as a type violation: null optionals are tolerated and
            // an unknown shape is something this structural check has no opinion on.
            if (kind is JsonKind.Null or JsonKind.Unknown)
            {
                continue;
            }

            if (!targets.Any(target => Matches(target, kind)))
            {
                reason = $"Property '{key}' should be of type {string.Join(" or ", targets)}.";
                return true;
            }
        }

        return false;
    }

    private static bool TryFindUnknownPropertyViolation(JsonElement schema,
        JsonElement properties,
        IDictionary<string, object?> arguments,
        out string reason)
    {
        reason = string.Empty;

        // Honor an explicit opt-in to extra properties; otherwise, because these are the app's own and MCP tool schemas
        // that enumerate their inputs, an argument the schema does not describe is treated as a hallucinated key and
        // rejected so the model gets told to drop it. (Stricter than JSON Schema's additionalProperties-defaults-true.)
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is JsonValueKind.True or JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in arguments.Keys)
        {
            if (!properties.TryGetProperty(key, out _))
            {
                reason = $"Unknown property '{key}' is not part of this tool's schema.";
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SchemaTypes(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object || !propertySchema.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String when type.GetString() is { Length: > 0 } single => [single],
            JsonValueKind.Array => type.EnumerateArray()
                                       .Where(static item => item.ValueKind == JsonValueKind.String)
                                       .Select(static item => item.GetString()!)
                                       .Where(static name => !string.IsNullOrEmpty(name))
                                       .ToList(),
            _ => []
        };
    }

    private static bool Matches(string schemaType, JsonKind kind)
    {
        return schemaType switch
        {
            "string" => kind == JsonKind.String,
            "boolean" => kind == JsonKind.Boolean,
            "integer" => kind == JsonKind.Integer,
            "number" => kind is JsonKind.Number or JsonKind.Integer,
            "array" => kind == JsonKind.Array,
            "object" => kind == JsonKind.Object,
            "null" => kind == JsonKind.Null,
            _ => true
        };
    }

    private static bool TryParseBoolean(string text, out bool value)
    {
        var trimmed = text.Trim();
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryParseNumber(string text, bool preferInteger, out object value)
    {
        var trimmed = text.Trim();
        if (preferInteger && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asLong))
        {
            value = asLong;
            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
        {
            // An integer target that arrived as "5.0": keep it integral so the downstream type check is satisfied.
            if (preferInteger && double.IsInteger(asDouble))
            {
                value = (long)asDouble;
                return true;
            }

            value = asDouble;
            return true;
        }

        value = 0d;
        return false;
    }

    private static string? AsString(object? value)
    {
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    private static JsonKind KindOf(object? value)
    {
        switch (value)
        {
            case null:
                return JsonKind.Null;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Object => JsonKind.Object,
                    JsonValueKind.Array => JsonKind.Array,
                    JsonValueKind.String => JsonKind.String,
                    JsonValueKind.Number => element.TryGetInt64(out _) ? JsonKind.Integer : JsonKind.Number,
                    JsonValueKind.True or JsonValueKind.False => JsonKind.Boolean,
                    JsonValueKind.Null => JsonKind.Null,
                    _ => JsonKind.Unknown
                };
            case bool:
                return JsonKind.Boolean;
            case string:
                return JsonKind.String;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return JsonKind.Integer;
            case float or double or decimal:
                return JsonKind.Number;
            case System.Collections.IEnumerable:
                return JsonKind.Array;
            default:
                return JsonKind.Object;
        }
    }

    private enum JsonKind
    {
        Null,
        Boolean,
        Integer,
        Number,
        String,
        Array,
        Object,
        Unknown
    }
}
