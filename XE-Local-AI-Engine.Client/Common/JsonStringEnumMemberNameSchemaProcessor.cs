namespace XE_Local_AI_Engine.Client.Common;

using System.Reflection;
using System.Text.Json.Serialization;
using NJsonSchema.Generation;

/// <summary>
///     Rewrites generated enum schema values to honor <see cref="JsonStringEnumMemberNameAttribute" />.
/// </summary>
/// <remarks>
///     The NJsonSchema generator FastEndpoints.Swagger uses emits the CLR member names (<c>Running</c>) as the OpenAPI
///     enum values, while the runtime <see cref="JsonStringEnumConverter{T}" /> serializes the attribute value
///     (<c>running</c>); the React zod validators then reject valid responses as an "unexpected shape", as the
///     host-agent runtime-status enums (state/desiredState/runtimeLifecycle) showed. For an enum carrying the
///     attribute the schema's values become the attribute values; enums without it are left untouched.
/// </remarks>
public sealed class JsonStringEnumMemberNameSchemaProcessor : ISchemaProcessor
{
    public void Process(SchemaProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var type = context.ContextualType.Type;
        if (!type.IsEnum)
        {
            return;
        }

        var schema = context.Schema;
        if (schema.Enumeration.Count == 0)
        {
            return;
        }

        var memberNameToWireName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            if (attribute is not null)
            {
                memberNameToWireName[field.Name] = attribute.Name;
            }
        }

        if (memberNameToWireName.Count == 0)
        {
            return;
        }

        var rewritten = schema.Enumeration
                              .Select(value => value is string memberName && memberNameToWireName.TryGetValue(memberName, out var wireName)
                                  ? wireName
                                  : value)
                              .ToList();

        schema.Enumeration.Clear();
        foreach (var value in rewritten)
        {
            schema.Enumeration.Add(value);
        }
    }
}
