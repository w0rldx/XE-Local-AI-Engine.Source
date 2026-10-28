namespace XE_Local_AI_Engine.Client.Hosting;

internal enum LaunchMode
{
    Headless,
    Desktop,
    McpOnly
}

internal static class LaunchModeExtensions
{
    internal static bool IsLocalMode(this LaunchMode mode) =>
        mode is LaunchMode.Desktop or LaunchMode.McpOnly;
}
