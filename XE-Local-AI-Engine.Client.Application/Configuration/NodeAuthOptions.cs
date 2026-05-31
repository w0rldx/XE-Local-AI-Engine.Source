namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration options for node auth behavior.
/// </summary>
public sealed class NodeAuthOptions
{
    public const string SectionName = "NodeAuth";

    public NodeJwtOptions Jwt { get; set; } = new();

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>
///     Configuration options for node jwt behavior.
/// </summary>
public sealed class NodeJwtOptions
{
    [Required]
    public string Issuer { get; set; } = "xe-local-ai-engine";

    [Required]
    public string Audience { get; set; } = "xe-local-ai-engine";

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;
}
