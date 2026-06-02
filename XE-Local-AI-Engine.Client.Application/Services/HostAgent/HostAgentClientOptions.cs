namespace XE_Local_AI_Engine.Client.Services.HostAgent;

public sealed record HostAgentClientOptions
{
    public const string SectionName = "HostAgent:Client";
    public const int DefaultBucketSeconds = 15;

    public string SocketPath { get; init; } = Environment.GetEnvironmentVariable("XE_HOST_AGENT_SOCKET")
                                              ?? "/run/host-agent/host-agent.sock";

    public string Secret { get; init; } = string.Empty;

    public string SecretFile { get; init; } = Environment.GetEnvironmentVariable("XE_HOST_AGENT_HMAC_SECRET_FILE")
                                              ?? "/etc/host-agent/hmac-secret";

    public int BucketSeconds { get; init; } = DefaultBucketSeconds;

    public static HostAgentClientOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection(SectionName).Get<HostAgentClientOptions>() ?? new HostAgentClientOptions();

        return configured with
        {
            Secret = string.IsNullOrWhiteSpace(configured.Secret) ? ReadSecret(configured.SecretFile) : configured.Secret.Trim()
        };
    }

    private static string ReadSecret(string secretFile)
    {
        return !string.IsNullOrWhiteSpace(secretFile) && File.Exists(secretFile)
            ? File.ReadAllText(secretFile).Trim()
            : string.Empty;
    }
}
