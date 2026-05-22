namespace XE_Local_AI_Engine.HostAgent.Linux.Hosting;

using System.Text;

public sealed record HostAgentSocketOptions
{
    public const UnixFileMode DefaultSocketFileMode = UnixFileMode.UserRead
                                                      | UnixFileMode.UserWrite
                                                      | UnixFileMode.GroupRead
                                                      | UnixFileMode.GroupWrite;

    // The Linux kernel limits sockaddr_un.sun_path to 108 bytes (including the
    // terminating NUL). Resolved paths at or above this length crash Kestrel at
    // bind with a cryptic ArgumentOutOfRangeException, so fail fast with a clear error.
    public const int SunPathMaxBytes = 108;

    public required string SocketPath { get; init; }

    public UnixFileMode SocketFileMode { get; init; } = DefaultSocketFileMode;

    public static HostAgentSocketOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredPath = configuration["HostAgent:SocketPath"]
                             ?? Environment.GetEnvironmentVariable("XE_HOST_AGENT_SOCKET");

        var socketPath = string.IsNullOrWhiteSpace(configuredPath)
            ? HostAgentSocketPaths.GetDefaultSocketPath()
            : configuredPath;

        EnsureWithinSunPathLimit(socketPath);

        return new HostAgentSocketOptions
        {
            SocketPath = socketPath
        };
    }

    private static void EnsureWithinSunPathLimit(string socketPath)
    {
        var byteLength = Encoding.UTF8.GetByteCount(socketPath);
        if (byteLength >= SunPathMaxBytes)
        {
            throw new InvalidOperationException(
                $"HostAgent socket path '{socketPath}' is {byteLength} bytes, which meets or exceeds the Unix socket sun_path limit of {SunPathMaxBytes} bytes. Configure a shorter HostAgent:SocketPath (or XE_HOST_AGENT_SOCKET).");
        }
    }
}
