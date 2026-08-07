namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     Binds the model-supplied arguments to a tool's declared parameters and substitutes them into templates.
///     <para>
///         Two invariants make substitution injection-proof: a placeholder always expands into exactly ONE value (one
///         argv element, or one URL path-segment/query-value — never a shell string, never whitespace-split), and every
///         <c>{token}</c> in a template must name a declared parameter that the model actually supplied. An undeclared
///         placeholder, a missing value, or a value whose JSON kind does not match the declared type is a fail-closed
///         rejection, not a best-effort substitution.
///     </para>
/// </summary>
internal static partial class CustomToolTemplate
{
    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    ///     Parses <paramref name="jsonArguments" /> and, for every declared parameter it carries, enforces the declared
    ///     <c>type</c> (a <c>number</c>/<c>integer</c> must be a JSON number, a <c>boolean</c> a JSON bool) and yields
    ///     its culture-invariant string form. A required parameter that is absent is a rejection; an optional absent one
    ///     is simply omitted (a template that references it will then fail closed). Arguments not matching any declared
    ///     parameter are ignored (the arg-repair wrapper already rejects unknown keys upstream).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BindAndEnforce(string jsonArguments, IReadOnlyList<CustomToolParameter> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (declared.Count == 0)
        {
            return values;
        }

        Dictionary<string, JsonElement> supplied;
        try
        {
            supplied = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                           string.IsNullOrWhiteSpace(jsonArguments) ? "{}" : jsonArguments,
                           CustomToolJson.Options)
                       ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new CustomToolExecutionException("The tool arguments were not valid JSON.", exception);
        }

        foreach (var parameter in declared)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            if (!supplied.TryGetValue(parameter.Name, out var element))
            {
                if (parameter.Required)
                {
                    throw new CustomToolExecutionException($"Required parameter '{parameter.Name}' was not supplied.");
                }

                continue;
            }

            values[parameter.Name] = EnforceType(parameter, element);
        }

        return values;
    }

    /// <summary>
    ///     Substitutes <paramref name="boundValues" /> into <paramref name="template" />, treating the whole result as a
    ///     single value. <paramref name="encode" /> is applied to each substituted value (URL-encoding for URL positions
    ///     so a value can never break out of a path segment or query value; identity for argv/body positions). Every
    ///     <c>{token}</c> must name a member of <paramref name="declaredNames" /> that has a bound value.
    /// </summary>
    public static string Substitute(string template,
        IReadOnlyDictionary<string, string> boundValues,
        IReadOnlySet<string> declaredNames,
        Func<string, string>? encode = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(boundValues);
        ArgumentNullException.ThrowIfNull(declaredNames);

        return PlaceholderRegex().Replace(template, match =>
        {
            var token = match.Groups["name"].Value;
            if (!declaredNames.Contains(token))
            {
                // Fail closed: a template can only ever reference parameters the tool declares.
                throw new CustomToolExecutionException($"Template references undeclared placeholder '{token}'.");
            }

            if (!boundValues.TryGetValue(token, out var value))
            {
                throw new CustomToolExecutionException($"No value was supplied for placeholder '{token}'.");
            }

            return encode is null ? value : encode(value);
        });
    }

    /// <summary>The set of placeholder names a template references, for validation and parameterized-host detection.</summary>
    public static IReadOnlySet<string> ReferencedPlaceholders(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in PlaceholderRegex().Matches(template).Cast<Match>())
        {
            names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    private static string EnforceType(CustomToolParameter parameter, JsonElement element)
    {
        var type = parameter.Type.Trim();

        if (string.Equals(type, "number", StringComparison.OrdinalIgnoreCase))
        {
            if (element.ValueKind != JsonValueKind.Number)
            {
                throw new CustomToolExecutionException($"Parameter '{parameter.Name}' must be a number.");
            }

            return element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        }

        if (string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase))
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var integer))
            {
                throw new CustomToolExecutionException($"Parameter '{parameter.Name}' must be an integer.");
            }

            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new CustomToolExecutionException($"Parameter '{parameter.Name}' must be a boolean.");
            }

            return element.GetBoolean() ? "true" : "false";
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new CustomToolExecutionException($"Parameter '{parameter.Name}' must be a string.");
        }

        return element.GetString() ?? string.Empty;
    }
}
