namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>Reads an Agent node's <c>outputSchema</c>: the small, flat JSON-Schema subset its session must answer in.</summary>
/// <remarks>
///     A flat object of <c>string | number | integer | boolean</c> members, with <c>enum</c>, <c>unmetOn</c> and <c>maxLength</c> on
///     strings and nothing else. Small on purpose: conditions read scalars by dot path, a flat enum is what makes <c>GRAPH-C4-5</c>
///     decidable at save, and anything outside the subset is refused rather than ignored. See docs/wiki/25-dev-workflows.md.
/// </remarks>
internal static class DevWorkflowOutputSchema
{
    /// <summary>The most members one output object may declare. The whole contract is rendered into the objective.</summary>
    internal const int MaxProperties = 16;

    /// <summary>The most values one enum member may list.</summary>
    internal const int MaxEnumValues = 32;

    /// <summary>The longest one enum value or member name may be.</summary>
    internal const int MaxTokenLength = 64;

    /// <summary>The longest the rendered contract may be, in characters, measured on <see cref="WorkSessionOutputSchema.Render" />.</summary>
    /// <remarks>
    ///     The contract lands in the objective uncapped, counted against <c>DevWorkflowObjectiveComposer.MaxObjectiveCharacters</c>
    ///     (7000) before policy gets the rest, so the member caps above alone would let it reach ~35K and refuse every run.
    ///     2000 is under a third of that ceiling: it leaves the node's instructions, what was asked and a full 4096-character
    ///     rule set body room beside it, and is still three times what the dev-intake-v1 classifier renders.
    /// </remarks>
    internal const int MaxRenderedLength = 2000;

    private static readonly string[] SchemaKeys = ["type", "properties", "required", "additionalProperties"];
    private static readonly string[] PropertyKeys = ["type", "enum", "unmetOn", "maxLength"];

    /// <summary>The node's output contract, or null when it declares none. Refused on any node type but <c>Agent</c>.</summary>
    public static WorkSessionOutputSchema? Parse(JsonElement node, string nodeKey, DevWorkflowNodeType nodeType)
    {
        if (!node.TryGetProperty("outputSchema", out var schema) || schema.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var owner = $"The 'outputSchema' on node '{nodeKey}'";
        if (nodeType != DevWorkflowNodeType.Agent)
        {
            throw new DevWorkflowValidationException($"Node '{nodeKey}' declares an 'outputSchema' but is a {nodeType} node, and only an Agent node's session hands in an output.");
        }

        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            throw new DevWorkflowValidationException($"{owner} must be an object schema whose 'type' is \"object\".");
        }

        EnsureOnly(schema, SchemaKeys, owner);
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
        {
            throw new DevWorkflowValidationException($"{owner} may set 'additionalProperties' only to false, which it always is: the output carries the declared members and no others.");
        }

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object || !properties.EnumerateObject().Any())
        {
            throw new DevWorkflowValidationException($"{owner} needs a non-empty 'properties' object.");
        }

        var required = ParseRequired(schema, owner);
        var parsed = new List<WorkSessionOutputProperty>();
        foreach (var property in properties.EnumerateObject())
        {
            if (parsed.Count == MaxProperties)
            {
                throw new DevWorkflowValidationException($"{owner} declares more than {MaxProperties} members.");
            }

            // JsonDocument keeps a repeated key, so a second declaration would silently shadow the first.
            if (parsed.Exists(declared => string.Equals(declared.Name, property.Name, StringComparison.Ordinal)))
            {
                throw new DevWorkflowValidationException($"{owner} declares the member '{property.Name}' more than once.");
            }

            parsed.Add(ParseProperty(property, required.Contains(property.Name), owner));
        }

        if (required.FirstOrDefault(name => !parsed.Exists(property => string.Equals(property.Name, name, StringComparison.Ordinal))) is { } undeclared)
        {
            throw new DevWorkflowValidationException($"{owner} requires '{undeclared}', which its 'properties' do not declare.");
        }

        var contract = new WorkSessionOutputSchema
        {
            Properties = parsed
        };

        // Measured on Render, the text the objective actually carries, so the bound cannot drift from what is sent.
        var rendered = contract.Render().Length;
        return rendered <= MaxRenderedLength
            ? contract
            : throw new DevWorkflowValidationException($"{owner} renders an output contract of {rendered} characters, which is too long for the objective: "
                                                       + $"at most {MaxRenderedLength}. Declare fewer members, shorter member names, or fewer and shorter enum values.");
    }

    private static HashSet<string> ParseRequired(JsonElement schema, string owner)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("required", out var declared) || declared.ValueKind == JsonValueKind.Null)
        {
            return required;
        }

        if (declared.ValueKind != JsonValueKind.Array
            || declared.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.String || !required.Add(entry.GetString()!)))
        {
            throw new DevWorkflowValidationException($"{owner} must list 'required' as an array of distinct member names.");
        }

        return required;
    }

    private static WorkSessionOutputProperty ParseProperty(JsonProperty property, bool required, string owner)
    {
        // A member name is one dot-path segment, so 'output.<name>' is a path a condition can actually write.
        var name = property.Name;
        if (name.Length > MaxTokenLength || name.Contains('.', StringComparison.Ordinal) || !GraphWorkflowTokens.IsDotPath(name))
        {
            throw new DevWorkflowValidationException($"{owner} declares a member '{name}', which is not a name a condition path can read: "
                                                     + $"at most {MaxTokenLength} characters, with no '.', whitespace or [ ] * ( ).");
        }

        var member = $"{owner} member '{name}'";
        var schema = property.Value;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException($"{member} must be an object.");
        }

        EnsureOnly(schema, PropertyKeys, member);
        // JSON Schema's own lower-case spelling only, so what saves here is what any schema reader would accept.
        var typeName = schema.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
        if (typeName is not ("string" or "number" or "integer" or "boolean") || !GraphWorkflowTokens.TryParseName<WorkSessionOutputType>(typeName, out var type))
        {
            throw new DevWorkflowValidationException($"{member} needs a 'type' of \"string\", \"number\", \"integer\" or \"boolean\"; the output is flat, so there is no object or array.");
        }

        var values = ParseEnum(schema, type, member);
        var maxLength = ParseMaxLength(schema, type, member);
        // A value longer than the bound is an answer Violation refuses every time, and GRAPH-C4-5 would still demand an edge for it.
        if (values?.Find(value => value.Length > maxLength) is { } tooLong)
        {
            throw new DevWorkflowValidationException($"{member} lists \"{tooLong}\" in its 'enum', which is longer than its 'maxLength' of {maxLength}, "
                                                     + "so the agent could never answer it. Raise the 'maxLength' or drop it.");
        }

        return new WorkSessionOutputProperty
        {
            Name = name,
            Type = type,
            Required = required,
            Enum = values,
            MaxLength = maxLength,
            UnmetOn = ParseUnmetOn(schema, values, member)
        };
    }

    /// <summary>The enum values that block the node run as "objective not met" instead of routing: a non-empty subset of the member's own enum.</summary>
    private static List<string>? ParseUnmetOn(JsonElement schema, List<string>? values, string member)
    {
        if (!schema.TryGetProperty("unmetOn", out var declared))
        {
            return null;
        }

        if (values is null || declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() == 0)
        {
            throw UnmetOnRefusal(member);
        }

        var unmet = new List<string>();
        foreach (var entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } value || !values.Contains(value, StringComparer.Ordinal) || unmet.Contains(value, StringComparer.Ordinal))
            {
                throw UnmetOnRefusal(member);
            }

            unmet.Add(value);
        }

        return unmet;
    }

    private static List<string>? ParseEnum(JsonElement schema, WorkSessionOutputType type, string member)
    {
        if (!schema.TryGetProperty("enum", out var declared))
        {
            return null;
        }

        if (type != WorkSessionOutputType.String || declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() is 0 or > MaxEnumValues)
        {
            throw EnumRefusal(member);
        }

        var values = new List<string>();
        foreach (var entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 and <= MaxTokenLength } value || values.Contains(value, StringComparer.Ordinal))
            {
                throw EnumRefusal(member);
            }

            values.Add(value);
        }

        return values;
    }

    private static int? ParseMaxLength(JsonElement schema, WorkSessionOutputType type, string member)
    {
        if (!schema.TryGetProperty("maxLength", out var declared))
        {
            return null;
        }

        // TryGetInt32 throws rather than answering false on a non-number, so the kind is checked first.
        return type == WorkSessionOutputType.String && declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out var maxLength) &&
               maxLength is > 0 and <= WorkSessionToolDefinitions.TextMaxLength
            ? maxLength
            : throw new DevWorkflowValidationException($"{member} may carry a 'maxLength' only as a string member, a whole number from 1 to {WorkSessionToolDefinitions.TextMaxLength}.");
    }

    private static DevWorkflowValidationException EnumRefusal(string member) =>
        new($"{member} may carry an 'enum' only as a string member, listing 1 to {MaxEnumValues} distinct, non-empty strings of at most {MaxTokenLength} characters.");

    private static DevWorkflowValidationException UnmetOnRefusal(string member) =>
        new($"{member} may carry an 'unmetOn' only beside an 'enum', listing at least one distinct value of that enum.");

    private static void EnsureOnly(JsonElement schema, string[] allowed, string owner)
    {
        if (schema.EnumerateObject().Select(static member => member.Name).FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal)) is { } unknown)
        {
            throw new DevWorkflowValidationException($"{owner} uses '{unknown}', which the output schema subset does not support; it allows {string.Join(", ", allowed)}.");
        }
    }
}
