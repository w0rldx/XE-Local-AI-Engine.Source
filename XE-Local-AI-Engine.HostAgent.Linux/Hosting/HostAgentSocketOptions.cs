namespace XE_Local_AI_Engine.HostAgent.Linux.Hosting;

public sealed record HostAgentSocketOptions
{
    public const UnixFileMode DefaultSocketFileMode = UnixFileMode.UserRead
                                                      | UnixFileMode.UserWrite
                                                      | UnixFileMode.GroupRead
                                                      | UnixFileMode.GroupWrite;

    public required string SocketPath { get; init; }

    public UnixFileMode SocketFileMode { get; init; } = DefaultSocketFileMode;

    public static HostAgentSocketOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredPath = configuration["HostAgent:SocketPath"]
                             ?? Environment.GetEnvironmentVariable("XE_HOST_AGENT_SOCKET");

        return new HostAgentSocketOptions
        {
            SocketPath = string.IsNullOrWhiteSpace(configuredPath)
                ? HostAgentSocketPaths.GetDefaultSocketPath()
                : configuredPath
        };
    }
}
