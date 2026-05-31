namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Configuration options for host agent docker behavior.
/// </summary>
public sealed class HostAgentDockerOptions
{
    public string Endpoint { get; set; } = GetDefaultRootlessDockerEndpoint();

    public bool UseFakeDriver { get; set; }

    public static void Bind(HostAgentDockerOptions options, IConfiguration configuration)
    {
        configuration.GetSection("HostAgent:Docker").Bind(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            options.Endpoint = GetDefaultRootlessDockerEndpoint();
        }
    }

    public static string GetDefaultRootlessDockerEndpoint()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return $"unix://{Path.Combine(runtimeDirectory, "docker.sock")}";
        }

        var userId = Environment.GetEnvironmentVariable("UID") ?? "1000";
        return $"unix:///run/user/{userId}/docker.sock";
    }
}
