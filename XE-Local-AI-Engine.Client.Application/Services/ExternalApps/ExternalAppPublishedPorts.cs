namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Text.Json;

/// <summary>
///     Reads and writes the <c>PublishedPortsJson</c> column. A JSON OBJECT keyed <c>"&lt;service&gt;:&lt;containerPort&gt;"</c>
///     rather than an array, because the store seeds a new row with <c>{}</c> and an empty array would not parse.
/// </summary>
public static class ExternalAppPublishedPorts
{
    /// <summary>The value the store seeds and the value an instance with nothing published carries.</summary>
    public const string Empty = "{}";

    /// <summary>Serialises the observed bindings. Ordinal key order, so two equal port sets serialise identically.</summary>
    public static string Serialize(IReadOnlyList<ExternalAppPublishedPort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var map = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            map[Key(port.Service, port.ContainerPort)] = port.HostPort;
        }

        return JsonSerializer.Serialize(map, ExternalAppJson.Options);
    }

    /// <summary>
    ///     Parses a stored value back. A malformed or absent document yields an empty list rather than throwing: the
    ///     column is read on every detail render, and a row written by an older build must not make the instance
    ///     unreadable.
    /// </summary>
    public static IReadOnlyList<ExternalAppPublishedPort> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        SortedDictionary<string, int>? map;
        try
        {
            map = JsonSerializer.Deserialize<SortedDictionary<string, int>>(json, ExternalAppJson.Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (map is null)
        {
            return [];
        }

        var ports = new List<ExternalAppPublishedPort>(map.Count);
        foreach (var entry in map)
        {
            var separator = entry.Key.LastIndexOf(':');
            if (separator <= 0
                || !int.TryParse(entry.Key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort))
            {
                continue;
            }

            ports.Add(new ExternalAppPublishedPort
            {
                Service = entry.Key[..separator],
                ContainerPort = containerPort,
                HostPort = entry.Value
            });
        }

        return ports;
    }

    private static string Key(string service, int containerPort)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{service}:{containerPort}");
    }
}
