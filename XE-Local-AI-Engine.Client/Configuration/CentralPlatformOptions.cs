namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class CentralPlatformOptions
{
    public const string SectionName = "CentralPlatform";

    [Required]
    public required string BaseUrl { get; set; }

    public string HubPath { get; set; } = "/hub/worker";

    public string PairingEndpoint { get; set; } = "/api/v1/client-nodes/pair";

    public string DeviceBindingStartEndpoint { get; set; } = "/api/v1/client-nodes/device-bind/start";

    public string DeviceBindingTokenEndpoint { get; set; } = "/api/v1/client-nodes/device-bind/token";

    public string WorkerTokenRefreshEndpoint { get; set; } = "/api/v1/client-nodes/worker-token/refresh";

    [Range(5, 300)]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public IReadOnlyList<int> ReconnectDelaysMs { get; init; } = [];

    [Range(1, 30000)]
    public int ReconnectBackoffBaseMs { get; set; } = 1000;

    [Range(1, 120000)]
    public int ReconnectBackoffMaxMs { get; set; } = 30000;

    [Range(0, 10000)]
    public int ReconnectBackoffJitterMs { get; set; } = 500;

    [Range(0, 100)]
    public int ReconnectMaxAttempts { get; set; } = 0;

    [Range(16, 1024)]
    public int MaxSignalRMessageSizeKb { get; set; } = 128;

    [Range(5, 600)]
    public int ToolCallTimeoutSeconds { get; set; } = 30;

    [Range(10, 3600)]
    public int InvocationTimeoutSeconds { get; set; } = 300;
}
