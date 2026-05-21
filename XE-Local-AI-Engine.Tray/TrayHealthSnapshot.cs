namespace XE_Local_AI_Engine.Tray;

internal sealed record TrayHealthSnapshot(
    string IconAssetName,
    string ToolTipText,
    bool IsReachable = false,
    string? State = null,
    string? DesiredState = null,
    string? WebUiUrl = null)
{
    private const string Healthy = "healthy";
    private const string Running = "running";
    private const string Stopped = "stopped";

    public static TrayHealthSnapshot Unreachable { get; } = new("tray-red.ico", "XE Local AI Engine - HostAgent unreachable");

    public bool IsDesiredStateRunning =>
        string.Equals(DesiredState, Running, StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(DesiredState)
            && string.Equals(State, Running, StringComparison.OrdinalIgnoreCase));

    public bool IsDesiredStateStopped =>
        string.Equals(DesiredState, Stopped, StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, Stopped, StringComparison.OrdinalIgnoreCase);

    public static TrayHealthSnapshot FromStatus(HostAgentStatusDto? status)
    {
        if (status is null)
        {
            return Unreachable;
        }

        if (IsStopped(status))
        {
            return CreateReachable("tray-gray.ico", "XE Local AI Engine - stopped by user", status);
        }

        if (IsHealthy(status))
        {
            return CreateReachable("tray-green.ico", "XE Local AI Engine - running and healthy", status);
        }

        return CreateReachable("tray-yellow.ico", BuildDegradedTooltip(status), status);
    }

    private static TrayHealthSnapshot CreateReachable(string iconAssetName, string toolTipText, HostAgentStatusDto status)
    {
        return new TrayHealthSnapshot(iconAssetName, toolTipText, true, status.State, status.DesiredState, status.WebUiUrl);
    }

    private static bool IsStopped(HostAgentStatusDto status)
    {
        return string.Equals(status.DesiredState, Stopped, StringComparison.OrdinalIgnoreCase)
               || string.Equals(status.State, Stopped, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHealthy(HostAgentStatusDto status)
    {
        return string.Equals(status.State, Running, StringComparison.OrdinalIgnoreCase)
               && string.Equals(status.Ollama, Healthy, StringComparison.OrdinalIgnoreCase)
               && string.Equals(status.WebServer, Healthy, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDegradedTooltip(HostAgentStatusDto status)
    {
        var state = Normalize(status.State, "unknown");
        var ollama = Normalize(status.Ollama, "unknown");
        var webServer = Normalize(status.WebServer, "unknown");
        return $"XE Local AI Engine - {state}; Ollama: {ollama}; Web UI: {webServer}";
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
