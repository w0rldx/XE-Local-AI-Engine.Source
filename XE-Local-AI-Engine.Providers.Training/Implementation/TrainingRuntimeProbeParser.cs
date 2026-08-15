namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Parses the handshake line <c>probe.py</c> emits.
/// </summary>
/// <remarks>
///     The probe's stdout is <b>not</b> clean, and assuming it is was the first thing that broke in live verification:
///     importing unsloth prints two banner lines ("Unsloth: Will patch your computer…", "Unsloth Zoo will now patch
///     everything…") before the probe writes anything. So the parser scans every captured line and takes the last one
///     that parses as a JSON object carrying <c>contractVersion</c>, rather than reading line 1 or the whole buffer.
/// </remarks>
internal static class TrainingRuntimeProbeParser
{
    /// <summary>
    ///     Returns the parsed handshake, or <see langword="null" /> when no line in <paramref name="lines" /> is one.
    /// </summary>
    public static TrainingRuntimeProbeReport? TryParse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var report = TryParseLine(lines[index]);
            if (report is not null)
            {
                return report;
            }
        }

        return null;
    }

    private static TrainingRuntimeProbeReport? TryParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("contractVersion", out var contractVersion)
                || contractVersion.ValueKind != JsonValueKind.Number
                || !contractVersion.TryGetInt32(out var version))
            {
                return null;
            }

            return new TrainingRuntimeProbeReport(version,
                ReadBool(root, "ready"),
                ReadString(root, "python"),
                ReadString(root, "torch"),
                ReadString(root, "unsloth"),
                ReadString(root, "bitsandbytes"),
                ReadBool(root, "cudaAvailable"),
                ReadString(root, "deviceName"),
                ReadString(root, "deviceCapability"),
                ReadErrors(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in errors.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                parsed[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return parsed;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
