namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Typed projection of a custom tool's opaque <c>ParametersJson</c> / <c>ConfigJson</c> (the persistence layer keeps
///     them as opaque strings; this layer owns their shape). Every type here is a deserialization target for the
///     operator-authored JSON and a serialization source, so the read and write halves of a tool's
///     config can never drift.
/// </summary>
internal static class CustomToolJson
{
    /// <summary>
    ///     The single options instance for custom-tool config (de)serialization. Case-insensitive so the camelCase wire
    ///     keys (<c>urlTemplate</c>, <c>isSecret</c>, …) bind to the PascalCase record members without per-member
    ///     attributes, and camelCase on write for a stable, operator-readable stored shape.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
///     A single declared input a Parameterized tool exposes to the model. <see cref="Type" /> is one of
///     <c>string</c>/<c>number</c>/<c>integer</c>/<c>boolean</c> and is enforced at substitution time (a number param
///     must arrive as a JSON number). A Fixed tool declares none. The declaration itself is not sensitive — only the
///     values the model supplies at run time are.
/// </summary>
internal sealed record CustomToolParameter(string Name, string Type, string Description, bool Required);

/// <summary>An HTTP header a fetch tool sends. <see cref="IsSecret" /> marks a value that must be value-scrubbed from any log/model-facing string.</summary>
internal sealed record CustomToolHeader(string Name, string Value, bool IsSecret);

/// <summary>An extra environment variable a command tool injects. <see cref="IsSecret" /> marks a value that must be value-scrubbed from tool output.</summary>
internal sealed record CustomToolEnvironmentVariable(string Name, string Value, bool IsSecret);

/// <summary>
///     Decrypted, typed <c>HttpFetch</c> configuration. <see cref="UrlTemplate" /> may carry <c>{param}</c> placeholders
///     in path/query positions only; when the host itself is parameterized <see cref="AllowedHosts" /> is mandatory (the
///     SSRF guard enforces membership). Secret header values are carried in the clear here (needed to build the request)
///     and scrubbed from anything the model or a log sees.
/// </summary>
internal sealed record HttpFetchConfig(
    string Method,
    string UrlTemplate,
    IReadOnlyList<CustomToolHeader> Headers,
    string? BodyTemplate,
    IReadOnlyList<string> AllowedHosts);

/// <summary>
///     Decrypted, typed <c>Command</c> configuration. <see cref="Executable" /> is a fixed absolute path (never a
///     <c>{param}</c>), validated at execution time; <see cref="ArgsTemplate" /> is one argv element per entry (a
///     <c>{param}</c> always substitutes into a single element, never a shell string); secret <see cref="Environment" />
///     values are injected via the child's environment (never argv) and scrubbed from its output.
/// </summary>
internal sealed record CommandConfig(
    string Executable,
    IReadOnlyList<string> ArgsTemplate,
    string? WorkingDirectory,
    int TimeoutSeconds,
    IReadOnlyList<CustomToolEnvironmentVariable> Env);

/// <summary>
///     Parses the opaque persisted JSON columns into the typed contracts above, normalizing absent collections to empty
///     so downstream code never null-checks a list. A malformed column throws <see cref="CustomToolConfigurationException" />
///     — the executor turns that into a non-throwing, scrubbed tool-failure result rather than letting it abort the run.
/// </summary>
internal static class CustomToolConfigParser
{
    public static IReadOnlyList<CustomToolParameter> ParseParameters(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<CustomToolParameter>>(parametersJson, CustomToolJson.Options);
            return parsed is null ? [] : NormalizeParameters(parsed);
        }
        catch (JsonException exception)
        {
            throw new CustomToolConfigurationException("The custom tool's parameter declaration is not valid JSON.", exception);
        }
    }

    public static HttpFetchConfig ParseHttpFetch(string configJson)
    {
        var raw = Deserialize<HttpFetchConfig>(configJson);
        return raw with
        {
            Method = raw.Method ?? string.Empty,
            UrlTemplate = raw.UrlTemplate ?? string.Empty,
            Headers = raw.Headers ?? [],
            AllowedHosts = raw.AllowedHosts ?? []
        };
    }

    public static CommandConfig ParseCommand(string configJson)
    {
        var raw = Deserialize<CommandConfig>(configJson);
        return raw with
        {
            Executable = raw.Executable ?? string.Empty,
            ArgsTemplate = raw.ArgsTemplate ?? [],
            Env = raw.Env ?? []
        };
    }

    private static T Deserialize<T>(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            throw new CustomToolConfigurationException("The custom tool's configuration is empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(configJson, CustomToolJson.Options)
                   ?? throw new CustomToolConfigurationException("The custom tool's configuration deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new CustomToolConfigurationException("The custom tool's configuration is not valid JSON.", exception);
        }
    }

    private static IReadOnlyList<CustomToolParameter> NormalizeParameters(IReadOnlyList<CustomToolParameter> parameters)
    {
        var normalized = new List<CustomToolParameter>(parameters.Count);
        foreach (var parameter in parameters)
        {
            normalized.Add(parameter with
            {
                Name = parameter.Name ?? string.Empty,
                Type = string.IsNullOrWhiteSpace(parameter.Type) ? "string" : parameter.Type,
                Description = parameter.Description ?? string.Empty
            });
        }

        return normalized;
    }
}

/// <summary>
///     Raised when a custom tool's persisted parameter/config JSON cannot be parsed into the typed contracts. The
///     executor catches it and returns a scrubbed, non-throwing tool-failure result, so a corrupt row is a failed tool
///     call rather than an aborted run.
/// </summary>
public sealed class CustomToolConfigurationException : Exception
{
    public CustomToolConfigurationException()
    {
    }

    public CustomToolConfigurationException(string message)
        : base(message)
    {
    }

    public CustomToolConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Raised when a custom-tool invocation is blocked by a security guard (SSRF denial, an undeclared placeholder, a
///     type mismatch, a rejected executable). The executor turns it into a non-throwing, secret-scrubbed tool-failure
///     result the model can read, rather than a throw that would count toward the run's abort threshold.
/// </summary>
public sealed class CustomToolExecutionException : Exception
{
    public CustomToolExecutionException()
    {
    }

    public CustomToolExecutionException(string message)
        : base(message)
    {
    }

    public CustomToolExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
