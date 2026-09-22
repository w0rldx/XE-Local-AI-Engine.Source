namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

internal sealed class DesktopStartupOptions
{
    public required string DataDirectory { get; init; }
    public required string ProfileDirectory { get; init; }
    public int? Port { get; init; }
    public Uri? Origin { get; init; }

    internal static DesktopStartupOptions Parse(string[] args, string? dataOverride, string localApplicationData)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--origin", StringComparer.Ordinal))
        {
            var probe = DesktopLaunchOptions.Parse(args);
            return new DesktopStartupOptions { DataDirectory = probe.ProfileDirectory, ProfileDirectory = probe.ProfileDirectory, Origin = probe.Origin };
        }

        int? port = null;
        var index = 0;
        while (index < args.Length)
        {
            var argument = args[index++];
            if (string.Equals(argument, "--desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--no-browser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? portValue = null;
            if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                portValue = argument[7..];
            }
            else if (string.Equals(argument, "--port", StringComparison.OrdinalIgnoreCase) && index < args.Length)
            {
                portValue = args[index++];
            }

            if (port is not null || !int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value is < 1 or > 65535)
            {
                throw new ArgumentException("Unsupported desktop arguments.", nameof(args));
            }

            port = value;
        }

        var data = NormalizeDirectory(string.IsNullOrWhiteSpace(dataOverride)
            ? Path.Combine(localApplicationData, "XE-Local-AI-Engine")
            : dataOverride);
        return new DesktopStartupOptions { DataDirectory = data, ProfileDirectory = Path.Combine(data, "desktop-profile"), Port = port };
    }

    internal static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A valid absolute data directory is required.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
