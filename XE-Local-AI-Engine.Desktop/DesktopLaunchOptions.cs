namespace XE_Local_AI_Engine.Desktop;

using System.Net;

internal readonly record struct DesktopLaunchOptions(Uri Origin, string ProfileDirectory)
{
    internal static DesktopLaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length != 4)
        {
            throw new ArgumentException("Both --origin and --profile-dir are required.", nameof(args));
        }

        string? originValue = null;
        string? profileDirectory = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            var value = args[index + 1];
            switch (args[index])
            {
                case "--origin" when originValue is null:
                    originValue = value;
                    break;
                case "--profile-dir" when profileDirectory is null:
                    profileDirectory = value;
                    break;
                default:
                    throw new ArgumentException("Only one --origin and one --profile-dir may be supplied.", nameof(args));
            }
        }

        return new DesktopLaunchOptions(ParseOrigin(originValue), ParseProfileDirectory(profileDirectory));
    }

    internal static NavigationDisposition ClassifyNavigation(Uri origin, Uri? request)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (request is null || !request.IsAbsoluteUri || !string.IsNullOrEmpty(request.UserInfo))
        {
            return NavigationDisposition.Blocked;
        }

        if (string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, request.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == request.Port)
        {
            return NavigationDisposition.SameOrigin;
        }

        return request.Scheme == Uri.UriSchemeHttp || request.Scheme == Uri.UriSchemeHttps
            ? NavigationDisposition.External
            : NavigationDisposition.Blocked;
    }

    private static Uri ParseOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath != "/"
            || origin.Port == 0
            || !IsLoopbackHost(origin.Host))
        {
            throw new ArgumentException("--origin must be an HTTP loopback origin without credentials, a path, query, or fragment.", nameof(value));
        }

        return origin;
    }

    private static string ParseProfileDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("--profile-dir must be an absolute path.", nameof(value));
        }

        return Path.GetFullPath(value);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

internal enum NavigationDisposition
{
    SameOrigin,
    External,
    Blocked
}
