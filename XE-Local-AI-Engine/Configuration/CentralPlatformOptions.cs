namespace XE_Local_AI_Engine.Configuration
{
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;

    public sealed class CentralPlatformOptions
    {
        public const string SectionName = "CentralPlatform";

        [Required]
        public required string BaseUrl { get; set; }

        public string HubPath { get; set; } = "/hub/worker";

        public string PairingEndpoint { get; set; } = "/api/v1/client-nodes/pair";

        [Range(5, 300)]
        public int HeartbeatIntervalSeconds { get; set; } = 30;

        public IReadOnlyList<int> ReconnectDelaysMs { get; init; } = [0, 2000, 5000, 10000, 30000];

        [Range(1, 100)]
        public int MaxReconnectAttempts { get; set; } = 10;

        [Range(16, 1024)]
        public int MaxSignalRMessageSizeKb { get; set; } = 128;

        [Range(5, 600)]
        public int ToolCallTimeoutSeconds { get; set; } = 30;

        [Range(10, 3600)]
        public int InvocationTimeoutSeconds { get; set; } = 300;
    }
}
