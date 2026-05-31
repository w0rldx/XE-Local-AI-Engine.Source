namespace XE_Local_AI_Engine.HostAgent.Windows;

/// <summary>
///     Configuration options for host agent linux grpc behavior.
/// </summary>
public sealed class HostAgentLinuxGrpcOptions
{
    public const string SectionName = "HostAgent:LinuxGrpc";
    public const string DefaultEndpoint = "http://127.0.0.1:57974";
    public const int DefaultBucketSeconds = 15;

    public string Endpoint { get; set; } = DefaultEndpoint;

    public string Secret { get; set; } = string.Empty;

    public string SecretFile { get; set; } = Environment.GetEnvironmentVariable("XE_HOST_AGENT_HMAC_SECRET_FILE") ?? string.Empty;

    public int BucketSeconds { get; set; } = DefaultBucketSeconds;

    public int MaxRetryAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public double BackoffMultiplier { get; set; } = 1.6D;

    public Uri EndpointUri =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("HostAgent:LinuxGrpc:Endpoint must be an absolute URI.");

    public static void Bind(HostAgentLinuxGrpcOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            options.Secret = ReadSecret(options.SecretFile);
        }

        options.Secret = options.Secret.Trim();
        Validate(options);
    }

    private static string ReadSecret(string secretFile)
    {
        return !string.IsNullOrWhiteSpace(secretFile) && File.Exists(secretFile)
            ? File.ReadAllText(secretFile).Trim()
            : string.Empty;
    }

    private static void Validate(HostAgentLinuxGrpcOptions options)
    {
        _ = options.EndpointUri;

        if (options.BucketSeconds <= 0)
        {
            throw new InvalidOperationException("HostAgent:LinuxGrpc:BucketSeconds must be positive.");
        }

        if (options.MaxRetryAttempts is < 2 or > 5)
        {
            throw new InvalidOperationException("HostAgent:LinuxGrpc:MaxRetryAttempts must be between 2 and 5.");
        }

        if (options.InitialBackoff <= TimeSpan.Zero || options.MaxBackoff < options.InitialBackoff)
        {
            throw new InvalidOperationException("HostAgent:LinuxGrpc backoff settings are invalid.");
        }

        if (options.BackoffMultiplier <= 1D)
        {
            throw new InvalidOperationException("HostAgent:LinuxGrpc:BackoffMultiplier must be greater than 1.");
        }
    }
}
