namespace XE_Local_AI_Engine.HostAgent.Linux.Hosting;

/// <summary>
///     Represents host agent socket paths.
/// </summary>
public static class HostAgentSocketPaths
{
    private const string RuntimeDirectoryName = "xe-host-agent";
    private const string SocketFileName = "host-agent.sock";

    public static string GetDefaultSocketPath()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            var userId = Environment.GetEnvironmentVariable("UID")
                         ?? throw new InvalidOperationException("XDG_RUNTIME_DIR is not set and UID is unavailable; cannot derive HostAgent socket path.");

            runtimeDirectory = Path.Combine("/run/user", userId);
        }

        return Path.Combine(runtimeDirectory, RuntimeDirectoryName, SocketFileName);
    }

    public static void PrepareSocketDirectory(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath)
                        ?? throw new InvalidOperationException("HostAgent socket path must include a directory.");

        Directory.CreateDirectory(directory);

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }
}
