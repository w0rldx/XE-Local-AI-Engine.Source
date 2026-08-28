namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;

/// <summary>The closed comparison set. No boolean algebra: two conditions are two edges into an <c>All</c> join.</summary>
internal enum DevWorkflowConditionOperator
{
    Eq,
    Ne,
    Gt,
    Gte,
    Lt,
    Lte,
    Exists,
    NotExists
}

/// <summary>
///     One declarative comparison against a node's output document, carried on an EDGE (never on a node — a node-level
///     condition would be a second way to say what a conditional edge already says).
///     <para>
///         <see cref="Path" /> is dot-separated property names and nothing else: no wildcards, no array indexing, no
///         functions. If a real expression language is ever justified it replaces <see cref="Evaluate" /> behind an
///         interface and nothing else changes.
///     </para>
/// </summary>
internal sealed record DevWorkflowCondition(string Path, DevWorkflowConditionOperator Operator, JsonElement Value)
{
    /// <summary>
    ///     Whether the edge carrying <paramref name="condition" /> fires. A null condition is unconditional.
    ///     <para>
    ///         Fail-closed on absence: a path the output does not carry answers <c>false</c> for every operator except
    ///         <see cref="DevWorkflowConditionOperator.NotExists" />, which is the one operator whose whole purpose is to
    ///         be true then. An edge must never fire on data that is not there — a node that produced no output at all
    ///         would otherwise route as if it had.
    ///     </para>
    /// </summary>
    public static bool Evaluate(DevWorkflowCondition? condition, JsonElement? output)
    {
        if (condition is null)
        {
            return true;
        }

        var resolved = output is { } document ? Resolve(document, condition.Path) : null;
        if (resolved is not { } value)
        {
            return condition.Operator == DevWorkflowConditionOperator.NotExists;
        }

        return condition.Operator switch
        {
            DevWorkflowConditionOperator.Exists => true,
            DevWorkflowConditionOperator.NotExists => false,
            DevWorkflowConditionOperator.Eq => Compare(value, condition.Value) == 0,
            DevWorkflowConditionOperator.Ne => Compare(value, condition.Value) is { } order && order != 0,
            DevWorkflowConditionOperator.Gt => Order(value, condition.Value) > 0,
            DevWorkflowConditionOperator.Gte => Order(value, condition.Value) >= 0,
            DevWorkflowConditionOperator.Lt => Order(value, condition.Value) < 0,
            _ => Order(value, condition.Value) <= 0
        };
    }

    /// <summary>Walks the document one property name at a time. A missing or non-object segment ends the walk.</summary>
    private static JsonElement? Resolve(JsonElement document, string path)
    {
        var current = document;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    ///     Equality order, or null when the two are not comparable at all. Booleans and null compare only for equality —
    ///     asking whether <c>true</c> is greater than <c>false</c> has no answer this runtime should invent.
    /// </summary>
    private static int? Compare(JsonElement left, JsonElement right)
    {
        if (left.ValueKind is JsonValueKind.True or JsonValueKind.False && right.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return left.ValueKind == right.ValueKind ? 0 : 1;
        }

        if (left.ValueKind == JsonValueKind.Null && right.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        return Order(left, right);
    }

    /// <summary>
    ///     Ordering for the four relational operators. Numbers compare numerically and strings ordinally; anything else,
    ///     including a type mismatch, is not an ordering and reads as "no" for every operator that asks for one.
    /// </summary>
    private static int? Order(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return left.GetDouble().CompareTo(right.GetDouble());
        }

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
        {
            return string.CompareOrdinal(left.GetString(), right.GetString());
        }

        return null;
    }

    /// <summary>
    ///     Reads one condition off an edge. Throws rather than degrading to "never fires": a definition whose condition
    ///     does not parse is a definition whose routing nobody can predict, and it is caught at save and again at run
    ///     start.
    /// </summary>
    public static DevWorkflowCondition Parse(JsonElement element, string edgeDescription)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} must be an object of {{path, op, value}}.");
        }

        var path = element.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} needs a non-empty 'path'.");
        }

        if (path.Split('.').Any(string.IsNullOrWhiteSpace))
        {
            throw new DevWorkflowValidationException($"The condition path '{path}' on edge {edgeDescription} has an empty segment.");
        }

        var op = element.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                 && Enum.TryParse<DevWorkflowConditionOperator>(opElement.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} needs an 'op' from "
                                                       + $"{string.Join(", ", Enum.GetNames<DevWorkflowConditionOperator>())}.");

        // Cloned: the JsonDocument the graph was parsed from is disposed before the first tick evaluates anything.
        var value = element.TryGetProperty("value", out var valueElement) ? valueElement.Clone() : default;
        if (op is DevWorkflowConditionOperator.Exists or DevWorkflowConditionOperator.NotExists)
        {
            return new DevWorkflowCondition(path, op, value);
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} uses '{op}' and so needs a 'value'.");
        }

        // A composite value is refused at authoring time rather than at run time, because Evaluate fails CLOSED: an
        // incomparable pair reads as "no", so the edge silently never fires and the run hangs with nothing in the log
        // to explain it. Scalars — including null, which compares equal to an explicit null — are left to Evaluate,
        // whose refusals for them are deliberate and tested. This one is not a refusal but an absence: there is no
        // comparison against an object or an array for it to make.
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} compares against a {value.ValueKind}. "
                                                     + "A condition value must be a scalar — a string, a number, a boolean or null.");
        }

        return new DevWorkflowCondition(path, op, value);
    }
}
