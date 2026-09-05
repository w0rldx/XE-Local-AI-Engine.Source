namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

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
            DevWorkflowConditionOperator.Lte => Order(value, condition.Value) <= 0,

            // Unreachable: Parse accepts named members only, so there is no ninth operator to fall through to. A
            // catch-all arm behaving as one of the eight would answer an operator nobody wrote with routing.
            _ => throw new InvalidOperationException($"Unknown dev workflow condition operator {condition.Operator}.")
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
            // Mirrors GraphWorkflowCondition.Order — there are two copies of this ladder, and a change to either is a
            // change to both. It is private there, so reusing it would mean editing the graph module to share it.
            //
            // Widest exact type first. Through double, 9007199254740992 and 9007199254740993 are the SAME value, so a
            // 'gt' over ids or byte counts past 2^53 answers on a rounding artefact rather than on the numbers. Double
            // is kept as the last resort for the tokens no exact arm reads — fractional and exponent forms out of
            // decimal range — and a token not even double reads is not an ordering at all.
            if (left.TryGetInt64(out var leftLong) && right.TryGetInt64(out var rightLong))
            {
                return leftLong.CompareTo(rightLong);
            }

            if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
            {
                return leftDecimal.CompareTo(rightDecimal);
            }

            // Past decimal's range an INTEGER token still has an exact value, and only a chain that ends at double
            // loses it: 1e29 and 1e29+1 are the same double. NumberStyles.AllowLeadingSign is the '^-?[0-9]+$' shape
            // itself — sign and digits, nothing else — so a fractional or exponent token simply does not parse here
            // and falls through to the line below.
            if (BigInteger.TryParse(left.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var leftInteger)
                && BigInteger.TryParse(right.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rightInteger))
            {
                return leftInteger.CompareTo(rightInteger);
            }

            // ponytail: fractional and exponent tokens beyond decimal range still round through double, so two that
            // differ only past ~17 significant digits read as equal and an integer token compared against its own
            // exponent form answers on the rounded pair. Upgrade path if that ever routes a real graph wrongly:
            // normalise each token into a BigInteger significand plus a base-10 exponent and compare those.
            return left.TryGetDouble(out var leftDouble) && right.TryGetDouble(out var rightDouble)
                ? leftDouble.CompareTo(rightDouble)
                : null;
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

        if (!GraphWorkflowTokens.IsDotPath(path))
        {
            throw new DevWorkflowValidationException($"The condition path '{path}' on edge {edgeDescription} is not a dot path. "
                                                     + "A dot path is property names separated by '.', with no wildcards, indexes or functions.");
        }

        // By NAME: Enum.TryParse would accept "3" or "-1" and hand back an operator no member has, which Evaluate
        // would then route on.
        var op = element.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                                                                 && GraphWorkflowTokens.TryParseName<DevWorkflowConditionOperator>(opElement.GetString(), out var parsed)
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

        // Two authoring-time refusals, both for the same reason: Evaluate fails CLOSED, so a comparison it can never
        // make is not an error anyone sees — it is an edge that silently never fires and a run that hangs with nothing
        // in the log to explain it. A comparison it CAN make and answers "no" is left alone; that is routing.
        //
        // There is no comparison against an object or an array to make at all.
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} compares against a {value.ValueKind}. "
                                                     + "A condition value must be a scalar — a string, a number, a boolean or null.");
        }

        // And booleans have no ordering: 'gt' against a boolean literal is dead for every possible output, not just
        // for the one this run produced. Equality still answers.
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && op is not (DevWorkflowConditionOperator.Eq or DevWorkflowConditionOperator.Ne))
        {
            throw new DevWorkflowValidationException($"The condition on edge {edgeDescription} asks whether a boolean is '{op}'. "
                                                     + "Booleans compare for equality only, so this edge could never fire.");
        }

        return new DevWorkflowCondition(path, op, value);
    }
}
