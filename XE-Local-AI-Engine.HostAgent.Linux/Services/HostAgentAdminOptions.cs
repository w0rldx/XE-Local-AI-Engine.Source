namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

/// <summary>
///     Configuration options for host agent admin behavior.
/// </summary>
public sealed class HostAgentAdminOptions
{
    public int Port { get; init; }

    public static HostAgentAdminOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredPort = configuration.GetValue<int?>("HostAgent:Admin:Port")
                             ?? ReadPortFromEnvironment()
                             ?? 0;

        if (configuredPort is < 0 or > 65535)
        {
            throw new InvalidOperationException("HostAgent:Admin:Port must be between 0 and 65535.");
        }

        return new HostAgentAdminOptions
        {
            Port = configuredPort
        };
    }

    private static int? ReadPortFromEnvironment()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("XE_HOST_AGENT_ADMIN_PORT"), out var port)
            ? port
            : null;
    }
}
