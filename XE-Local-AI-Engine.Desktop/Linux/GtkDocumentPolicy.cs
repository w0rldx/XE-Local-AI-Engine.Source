namespace XE_Local_AI_Engine.Desktop.Linux;

internal static class GtkDocumentPolicy
{
    internal const string UserAgentMarker = "XE-Native-Restricted/1";
    internal const string ContentSecurityPolicy = "frame-src 'none'; object-src 'none'";
    internal const string PermissionsPolicy = "microphone=(self), camera=(), display-capture=()";

    internal static bool IsTrusted(Uri origin, string? current, string? resource, uint status, string? mime,
        IReadOnlyList<string> contentSecurityPolicies, IReadOnlyList<string> permissionsPolicies) =>
        SameOrigin(origin, current) && SameOrigin(origin, resource)
        && status is >= 200 and < 300 && string.Equals(mime, "text/html", StringComparison.OrdinalIgnoreCase)
        && contentSecurityPolicies.Count == 1 && contentSecurityPolicies[0] == ContentSecurityPolicy
        && permissionsPolicies.Count == 1 && permissionsPolicies[0] == PermissionsPolicy;

    internal static bool SameOrigin(Uri origin, string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
        && DesktopLaunchOptions.ClassifyNavigation(origin, uri) == NavigationDisposition.SameOrigin;

    internal static bool AudioOnly(bool audio, bool video, bool display) => audio && !video && !display;
}
