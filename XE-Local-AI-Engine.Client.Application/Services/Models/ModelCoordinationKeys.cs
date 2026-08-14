namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Globalization;
using System.Text;

public static class ModelCoordinationKeys
{
    public static string NormalizeModelName(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var normalized = modelName.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The model name does not produce a valid coordination key.", nameof(modelName));
        }

        return normalized;
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.StartsWith('/')
            || System.IO.Path.IsPathRooted(normalized)
            || segments.Length == 0
            || segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("The member path must be a contained relative path.", nameof(relativePath));
        }

        return string.Join('/', segments).ToUpper(CultureInfo.InvariantCulture);
    }

    public static string Model(string modelName) =>
        $"0:model:{NormalizeModelName(modelName)}";

    public static string Path(string relativePath) =>
        $"1:path:{NormalizeRelativePath(relativePath)}";

    public static string ProviderMap(string modelName) =>
        $"2:provider-map:{NormalizeModelName(modelName)}";

    public static IReadOnlyList<string> NormalizeSet(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (result.Length == 0)
        {
            throw new ArgumentException("At least one coordination key is required.", nameof(keys));
        }

        return result;
    }
}
