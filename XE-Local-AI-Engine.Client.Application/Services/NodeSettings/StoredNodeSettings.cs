namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Value object carrying stored node settings data.
/// </summary>
public sealed record StoredNodeSettings
{
    public const int DefaultMaxMessageRequestTimeoutSeconds = 300;

    public const int MinMaxMessageRequestTimeoutSeconds = 5;

    public const int MaxMaxMessageRequestTimeoutSeconds = 3600;

    public int MaxMessageRequestTimeoutSeconds { get; init; } = DefaultMaxMessageRequestTimeoutSeconds;

    public string? DefaultModelName { get; init; }
}
