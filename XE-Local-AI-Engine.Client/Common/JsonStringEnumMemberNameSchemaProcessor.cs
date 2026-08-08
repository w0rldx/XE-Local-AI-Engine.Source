namespace XE_Local_AI_Engine.Client.Common;

using System.Reflection;
using System.Text.Json.Serialization;
using NJsonSchema.Generation;

/// <summary>
///     Rewrites generated enum schema values to honor <see cref="JsonStringEnumMemberNameAttribute" />.
///     <para>
///         The NJsonSchema generator used by FastEndpoints.Swagger emits the CLR member names
///         (e.g. <c>Running</c>) as the OpenAPI enum values, but the runtime
///         <see cref="JsonStringEnumConverter{T}" /> serializes the attribute value (e.g. <c>running</c>).
///         That mismatch makes the OpenAPI document — and every client generated from it (the React
///         zod response validators) — reject valid responses as an "unexpected shape" (observed on the
///         host-agent runtime-status enums: state/desiredState/runtimeLifecycle).
///     </para>
///     <para>
///         For each enum type carrying <see cref="JsonStringEnumMemberNameAttribute" /> on its members,
///         this processor replaces the schema's enumeration values with the attribute values so the
///         document matches the wire format. Enums without the attribute (member name == wire value)
///         are left untouched.
///     </para>
/// </summary>
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
