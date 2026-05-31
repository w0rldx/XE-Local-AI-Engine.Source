namespace XE_Local_AI_Engine.HostAgent.Linux.Capabilities;

/// <summary>
///     Configuration options for host agent capability behavior.
/// </summary>
public sealed class HostAgentCapabilityOptions
{
    public string RuntimeDataPath { get; set; } = "/var/lib/xe-host-agent";

    public static void Bind(HostAgentCapabilityOptions options, IConfiguration configuration)
    {
        configuration.GetSection("HostAgent:Capabilities").Bind(options);
        if (string.IsNullOrWhiteSpace(options.RuntimeDataPath))
        {
            options.RuntimeDataPath = "/var/lib/xe-host-agent";
        }
    }
}
