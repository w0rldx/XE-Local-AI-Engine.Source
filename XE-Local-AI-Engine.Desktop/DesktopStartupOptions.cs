namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

internal sealed class DesktopStartupOptions
{
    // Mirrors DesktopBootstrap.ApplicationDataFolderName in the managed Client (a separate assembly this shell does
    // not reference); kept in sync by hand so both processes use the same per-user root.
    internal const string ApplicationDataFolderName = "XE-Local-AI-Engine";

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
            if (string.Equals(argument, DesktopEngineSession.DesktopArgument, StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, DesktopEngineSession.NoBrowserArgument, StringComparison.OrdinalIgnoreCase))
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
            ? Path.Combine(localApplicationData, ApplicationDataFolderName)
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
