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

    [Range(minimum: 5, maximum: 300)]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public IReadOnlyList<int> ReconnectDelaysMs { get; init; } = [];

    [Range(minimum: 1, maximum: 30000)]
    public int ReconnectBackoffBaseMs { get; set; } = 1000;

    [Range(minimum: 1, maximum: 1800000)]
    public int ReconnectBackoffMaxMs { get; set; } = 1800000;

    [Range(minimum: 0, maximum: 10000)]
    public int ReconnectBackoffJitterMs { get; set; } = 500;

    [Range(minimum: 0, maximum: 100)]
    public int ReconnectMaxAttempts { get; set; } = 0;

    [Range(minimum: 5, maximum: 600)]
    public int ToolCallTimeoutSeconds { get; set; } = 30;

    [Range(minimum: 10, maximum: 3600)]
    public int InvocationTimeoutSeconds { get; set; } = 300;
}
