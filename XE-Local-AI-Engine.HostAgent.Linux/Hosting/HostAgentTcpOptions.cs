namespace XE_Local_AI_Engine.HostAgent.Linux.Hosting;

using System.Globalization;

public sealed class HostAgentTcpOptions
{
    public const string SectionName = "HostAgent:Tcp";
    public const int DefaultPort = 57974;

    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = DefaultPort;

    public static HostAgentTcpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new HostAgentTcpOptions();
        configuration.GetSection(SectionName).Bind(options);

        if (int.TryParse(Environment.GetEnvironmentVariable("XE_HOST_AGENT_TCP_PORT"), CultureInfo.InvariantCulture, out var port))
        {
            options.Port = port;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("XE_HOST_AGENT_TCP_DISABLED"), out var disabled) && disabled)
        {
            options.Enabled = false;
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("HostAgent:Tcp:Port must be between 1 and 65535.");
        }

        return options;
    }
}
