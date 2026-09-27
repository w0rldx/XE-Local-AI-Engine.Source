namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.Json;

/// <summary>
///     Parses the opaque persisted JSON columns into the typed contracts in <c>CustomToolContracts.cs</c>, normalizing absent collections to empty so downstream code
///     never null-checks a list.
/// </summary>
/// <remarks>
///     A malformed column throws <see cref="CustomToolConfigurationException" />, which the executor turns into a non-throwing, scrubbed
///     tool-failure result rather than letting it abort the run.
/// </remarks>
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
