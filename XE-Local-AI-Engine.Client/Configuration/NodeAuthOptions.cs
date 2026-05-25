namespace XE_Local_AI_Engine.Client.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class NodeAuthOptions
{
    public const string SectionName = "NodeAuth";

    public NodeJwtOptions Jwt { get; set; } = new();

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class NodeJwtOptions
{
    [Required]
    public string Issuer { get; set; } = "xe-local-ai-engine";

    [Required]
    public string Audience { get; set; } = "xe-local-ai-engine";

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;
}
